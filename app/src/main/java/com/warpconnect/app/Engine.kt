package com.warpconnect.app

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import mobileapi.Listener
import mobileapi.Mobileapi
import org.json.JSONArray
import org.json.JSONObject

typealias ProgressSink = (phase: String, completed: Int, total: Int, message: String) -> Unit

data class EndpointResult(
    val endpoint: String,
    val working: Boolean,
    val durable: Boolean,
    val tunnelPingMs: Double,
    val lossPercent: Double,
    val endpointPingMs: Double,
    val speedMbps: Double,
    val region: String,
    val node: String,
    val country: String,
    val nodeLocation: String,
) {
    val label: String
        get() = nodeLocation.ifBlank { region }
}

data class ScanReport(val awgI1: String, val results: List<EndpointResult>) {
    /** Durable endpoints first, then ones that worked but later went quiet. */
    fun candidates(limit: Int = 3): List<EndpointResult> {
        val durable = results.filter { it.working && it.durable }.sortedBy { it.tunnelPingMs }
        val torn = results.filter { it.working && !it.durable }.sortedBy { it.lossPercent }
        return (durable + torn).take(limit)
    }
}

data class JunkProfile(
    val junkCount: Int,
    val junkMin: Int,
    val junkMax: Int,
    val i1: String,
)

data class Registration(
    val accountJson: String,
    val endpoint: String,
    val i1: String,
    val i1Label: String,
    val jc: Int,
    val jmin: Int,
    val jmax: Int,
    val mtu: Int,
)

data class TunnelStats(
    val up: Boolean,
    val handshakeTime: Long,
    val handshakeAgeMs: Long,
    val rxBytes: Long,
    val txBytes: Long,
    val endpoint: String,
)

data class SocketFDs(val v4: Int, val v6: Int)

class EngineException(
    val code: String,
    override val message: String,
    val retryable: Boolean,
) : Exception(message)

/**
 * Thin, coroutine-friendly wrapper around the gomobile bridge. Nothing here
 * decides policy: it starts engine operations, parses their event stream and
 * surfaces tunnel statistics.
 */
object Engine {

    suspend fun registerThroughTunnel(
        options: JSONObject,
        progress: ProgressSink? = null,
    ): Registration {
        val payload = runOperation("Registration", progress) { listener ->
            Mobileapi.registerTunnel(options.toString(), listener)
        }
        val registration = Registration(
            accountJson = payload.optString("accountJson"),
            endpoint = payload.optString("endpoint"),
            i1 = payload.optString("i1"),
            i1Label = payload.optString("i1Label"),
            jc = payload.optInt("jc", Store.DEFAULT_JC),
            jmin = payload.optInt("jmin", Store.DEFAULT_JMIN),
            jmax = payload.optInt("jmax", Store.DEFAULT_JMAX),
            mtu = payload.optInt("mtu", Store.DEFAULT_MTU),
        )
        if (registration.accountJson.isBlank()) {
            throw EngineException(
                "registration_incomplete",
                "Registration finished without a usable account",
                true,
            )
        }
        return registration
    }

    suspend fun scan(
        accountJson: String,
        options: JSONObject,
        progress: ProgressSink? = null,
    ): ScanReport {
        val payload = runOperation("Scan", progress) { listener ->
            Mobileapi.start(request("scan", accountJson, options), listener)
        }
        return ScanReport(
            awgI1 = payload.optString("awgI1"),
            results = parseResults(payload.optJSONArray("results")),
        )
    }

    suspend fun findJunk(
        accountJson: String,
        options: JSONObject,
        progress: ProgressSink? = null,
    ): JunkProfile {
        val payload = runOperation("Deep search", progress) { listener ->
            Mobileapi.start(request("find-junk", accountJson, options), listener)
        }
        return JunkProfile(
            junkCount = payload.optInt("junkCount", Store.DEFAULT_JC),
            junkMin = payload.optInt("junkMin", Store.DEFAULT_JMIN),
            junkMax = payload.optInt("junkMax", Store.DEFAULT_JMAX),
            i1 = payload.optString("i1"),
        )
    }

    suspend fun generateI1Profile(profile: String, host: String = ""): String = withContext(Dispatchers.IO) {
        try {
            Mobileapi.generateI1Profile(profile, host)
        } catch (e: Exception) {
            throw EngineException("i1_failed", e.message ?: "could not build the packet profile", true)
        }
    }

    suspend fun tunnelUp(spec: JSONObject, tunFD: Int): TunnelStats = withContext(Dispatchers.IO) {
        val raw = try {
            Mobileapi.tunnelUp(spec.toString(), tunFD.toLong())
        } catch (e: Exception) {
            throw EngineException("tunnel_up_failed", e.message ?: "could not start the tunnel", true)
        }
        parseTunnelStats(raw)
    }

    suspend fun tunnelDown() {
        withContext(Dispatchers.IO) { tunnelDownNow() }
    }

    /**
     * Blocking teardown for callbacks that cannot suspend (service shutdown and
     * `onDestroy`). Leaving the device attached would keep the TUN interface up
     * after the service dies, which blackholes all traffic.
     */
    fun tunnelDownNow() {
        try {
            Mobileapi.tunnelDown()
        } catch (_: Exception) {
            // Already down is fine.
        }
    }

    suspend fun tunnelStats(): TunnelStats = withContext(Dispatchers.IO) {
        val raw = try {
            Mobileapi.tunnelStats()
        } catch (e: Exception) {
            throw EngineException("tunnel_stats_failed", e.message ?: "could not read tunnel state", true)
        }
        parseTunnelStats(raw)
    }

    suspend fun tunnelSocketFDs(): SocketFDs = withContext(Dispatchers.IO) {
        try {
            val json = JSONObject(Mobileapi.tunnelSocketFDs())
            SocketFDs(json.optInt("v4", -1), json.optInt("v6", -1))
        } catch (_: Exception) {
            SocketFDs(-1, -1)
        }
    }

    fun cancel() {
        runCatching { Mobileapi.cancel() }
    }

    private fun request(operation: String, accountJson: String, payload: JSONObject): String =
        JSONObject()
            .put("schemaVersion", 1)
            .put("operation", operation)
            .put("accountJson", accountJson)
            .put("payload", payload)
            .toString()

    private suspend fun runOperation(
        label: String,
        progress: ProgressSink?,
        block: (Listener) -> Unit,
    ): JSONObject = withContext(Dispatchers.IO) {
        ensureActive()
        var completed: JSONObject? = null
        var failure: EngineException? = null

        val listener = object : Listener {
            override fun onEvent(eventJSON: String) {
                val event = runCatching { JSONObject(eventJSON) }.getOrNull() ?: return
                when (event.optString("type")) {
                    "completed" -> completed = event.optJSONObject("payload")
                    "error" -> {
                        val error = event.optJSONObject("error")
                        failure = EngineException(
                            code = error?.optString("code").orEmpty().ifBlank { "engine_error" },
                            message = error?.optString("message").orEmpty().ifBlank { "$label failed" },
                            retryable = error?.optBoolean("retryable") ?: false,
                        )
                    }
                    "progress", "phase-completed", "speed" -> progress?.invoke(
                        event.optString("phase").ifBlank { label },
                        event.optInt("completed"),
                        event.optInt("total"),
                        event.optString("message"),
                    )
                    "endpoint" -> {
                        val found = event.optJSONObject("endpoint")
                        progress?.invoke("endpoint", 0, 0, found?.optString("endpoint").orEmpty())
                    }
                }
            }
        }

        try {
            block(listener)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            if (failure == null) {
                failure = EngineException("engine_failed", e.message ?: "$label failed", true)
            }
        }

        ensureActive()
        completed ?: throw failure ?: EngineException(
            "engine_no_result",
            "$label finished without a result",
            true,
        )
    }

    private fun parseResults(array: JSONArray?): List<EndpointResult> {
        if (array == null) return emptyList()
        val results = ArrayList<EndpointResult>(array.length())
        for (index in 0 until array.length()) {
            val json = array.optJSONObject(index) ?: continue
            val endpoint = json.optString("endpoint")
            if (endpoint.isBlank()) continue
            results += EndpointResult(
                endpoint = endpoint,
                working = json.optBoolean("working"),
                durable = json.optBoolean("durable"),
                tunnelPingMs = json.optDouble("tunnelPingMs", Double.MAX_VALUE),
                lossPercent = json.optDouble("lossPercent", 100.0),
                endpointPingMs = json.optDouble("endpointPingMs", 0.0),
                speedMbps = json.optDouble("speedMbps", 0.0),
                region = json.optString("region"),
                node = json.optString("node"),
                country = json.optString("country"),
                nodeLocation = json.optString("nodeLocation"),
            )
        }
        return results
    }

    private fun parseTunnelStats(raw: String?): TunnelStats {
        val json = runCatching { JSONObject(raw ?: "{}") }.getOrDefault(JSONObject())
        return TunnelStats(
            up = json.optBoolean("up"),
            handshakeTime = json.optLong("handshakeTime"),
            handshakeAgeMs = json.optLong("handshakeAgeMs"),
            rxBytes = json.optLong("rxBytes"),
            txBytes = json.optLong("txBytes"),
            endpoint = json.optString("endpoint"),
        )
    }
}
