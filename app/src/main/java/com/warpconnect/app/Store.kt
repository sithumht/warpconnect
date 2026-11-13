package com.warpconnect.app

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.wifi.WifiManager
import android.telephony.TelephonyManager
import org.json.JSONArray
import org.json.JSONObject

/**
 * The endpoint and packet-shaping parameters that carried real traffic on a
 * given network. Kept so a reconnect is instant instead of a fresh scan.
 */
data class Winner(
    val endpoint: String,
    val i1: String,
    val i1Label: String,
    val jc: Int,
    val jmin: Int,
    val jmax: Int,
    val mtu: Int,
    val savedAt: Long,
    val networkKey: String,
)

data class StatsRecommendation(
    val endpoint: String,
    val asn: String,
    val isp: String,
    val transport: String,
    val savedAt: Long,
)

class Store(context: Context) {
    private val app = context.applicationContext
    private val prefs = app.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    var accountJson: String?
        get() = prefs.getString(KEY_ACCOUNT, null)
        set(value) {
            if (value == null) {
                prefs.edit().remove(KEY_ACCOUNT).apply()
            } else {
                prefs.edit().putString(KEY_ACCOUNT, value).apply()
            }
        }

    fun winner(): Winner? {
        val raw = prefs.getString(KEY_WINNER, null) ?: return null
        return runCatching { decodeWinner(JSONObject(raw)) }.getOrNull()
    }

    /**
     * The most recent verified endpoints (newest first), deduplicated by
     * endpoint and honouring the winner TTL. Auto-heal retries these before
     * paying for a rescan; they are the "worked before on this network" set.
     */
    fun recentWinners(networkKey: String): List<Winner> {
        val candidates = mutableListOf<Winner>()
        winner()?.let { candidates += it }
        prefs.getString(KEY_WINNERS, null)?.let { raw ->
            runCatching {
                val array = JSONObject(raw).optJSONArray("items") ?: return@let
                for (i in 0 until array.length()) {
                    candidates += decodeWinner(array.getJSONObject(i))
                }
            }.onFailure { /* corrupt history is not fatal */ }
        }
        val now = System.currentTimeMillis()
        val seen = HashSet<String>()
        return candidates
            .asSequence()
            .filter { it.networkKey == networkKey && now - it.savedAt < WINNER_TTL_MS }
            .sortedByDescending { it.savedAt }
            .filter { seen.add(it.endpoint) }
            .take(MAX_WINNERS)
            .toList()
    }

    fun saveWinner(winner: Winner) {
        val merged = mutableListOf(winner)
        prefs.getString(KEY_WINNERS, null)?.let { raw ->
            runCatching {
                val array = JSONObject(raw).optJSONArray("items") ?: return@let
                for (i in 0 until array.length()) {
                    if (merged.size >= MAX_WINNERS) break
                    merged += decodeWinner(array.getJSONObject(i))
                }
            }.onFailure { /* corrupt history; replace it */ }
        }
        val deduped = merged.distinctBy { it.endpoint }.take(MAX_WINNERS)
        val history = JSONArray()
        deduped.forEach { history.put(encodeWinner(it)) }
        prefs.edit()
            .putString(KEY_WINNER, encodeWinner(winner).toString())
            .putString(KEY_WINNERS, JSONObject().put("items", history).toString())
            .apply()
    }

    var profilePermissionsAsked: Boolean
        get() = prefs.getBoolean(KEY_PROFILE_ASKED, false)
        set(value) {
            prefs.edit().putBoolean(KEY_PROFILE_ASKED, value).apply()
        }

    /**
     * Remember a failing endpoint for this network so later taps don't re-test
     * thousands of dead candidates. Success clears the silence. The deadline
     * keeps the silence bounded so re-verification can happen.
     */
    fun silenceEndpoint(networkKey: String, endpoint: String) {
        val map = silenceMap(networkKey)
        map[endpoint] = System.currentTimeMillis() + SILENCE_MS
        storeSilenceMap(networkKey, map)
    }

    fun isSilenced(networkKey: String, endpoint: String): Boolean =
        silenceMap(networkKey)[endpoint]?.takeIf { it > 0 }
            ?.let { it > System.currentTimeMillis() } ?: false

    fun unsilenceEndpoint(networkKey: String, endpoint: String) {
        val map = silenceMap(networkKey)
        if (map.remove(endpoint) != null) storeSilenceMap(networkKey, map)
    }

    fun silencedFor(networkKey: String): List<String> {
        val now = System.currentTimeMillis()
        return silenceMap(networkKey).filterValues { it == 0L || it > now }.keys.toList()
    }

    fun clearSilence(networkKey: String) {
        prefs.edit().remove(silenceKey(networkKey)).apply()
    }

    private fun silenceKey(networkKey: String) = "silenced_$networkKey"

    private fun silenceMap(networkKey: String): MutableMap<String, Long> {
        val raw = prefs.getString(silenceKey(networkKey), null) ?: return mutableMapOf()
        return runCatching {
            val array = JSONObject(raw).optJSONArray("items") ?: return mutableMapOf()
            buildMap {
                for (i in 0 until array.length()) {
                    val item = array.getJSONObject(i)
                    val ep = item.optString("ep")
                    val deadline = item.optLong("deadline")
                    if (ep.isNotBlank()) put(ep, deadline)
                }
            }.toMutableMap()
        }.getOrDefault(mutableMapOf())
    }

    private fun storeSilenceMap(networkKey: String, map: Map<String, Long>) {
        // Drop entries whose deadline already passed so the file doesn't grow forever.
        val now = System.currentTimeMillis()
        val fresh = map.filterValues { it == 0L || it > now }
        val array = JSONArray()
        for ((ep, deadline) in fresh) {
            array.put(JSONObject().put("ep", ep).put("deadline", deadline))
        }
        prefs.edit()
            .putString(silenceKey(networkKey), JSONObject().put("items", array).toString())
            .apply()
    }

    /**
     * Cached community endpoint recommendation for a given transport + ASN/ISP.
     * The stats server is usually only reachable from inside the tunnel on
     * strict networks, so caching the last answer avoids unreachable live calls.
     */
    fun statsRecommendation(): StatsRecommendation? {
        val raw = prefs.getString(KEY_STATS, null) ?: return null
        return runCatching {
            val json = JSONObject(raw)
            StatsRecommendation(
                endpoint = json.optString("endpoint"),
                asn = json.optString("asn"),
                isp = json.optString("isp"),
                transport = json.optString("transport"),
                savedAt = json.optLong("savedAt"),
            ).takeIf {
                it.endpoint.isNotBlank() &&
                    it.transport.isNotBlank() &&
                    System.currentTimeMillis() - it.savedAt < WINNER_TTL_MS
            }
        }.onFailure { prefs.edit().remove(KEY_STATS) }.getOrNull()
    }

    fun saveStatsRecommendation(value: StatsRecommendation) {
        val json = JSONObject()
            .put("endpoint", value.endpoint)
            .put("asn", value.asn)
            .put("isp", value.isp)
            .put("transport", value.transport)
            .put("savedAt", value.savedAt)
        prefs.edit().putString(KEY_STATS, json.toString()).apply()
    }

    fun clearWinner() {
        prefs.edit().remove(KEY_WINNER).remove(KEY_WINNERS).apply()
    }

    fun hasWinnerFor(networkKey: String, now: Long = System.currentTimeMillis()): Boolean {
        val winner = winner() ?: return false
        return winner.networkKey == networkKey && now - winner.savedAt < WINNER_TTL_MS
    }

    /**
     * A stable enough identity for "the network I am on right now". Must be
     * sampled before the VPN goes up, because afterwards the active network is
     * the VPN itself.
     *
     * - Wi-Fi: keyed to the SSID when the app can read it (location-scoped on
     *   modern Android), falling back to iface/gateway/DNS when not.
     * - Cell: keyed to the carrier name and MCC-MNC of the serving network.
     * - Anything else: falls back to the legacy iface/gateway/DNS signature.
     */
    fun networkKey(): String {
        val manager = app.getSystemService(ConnectivityManager::class.java) ?: return "unknown"
        val network = manager.activeNetwork ?: return "offline"
        val capabilities = manager.getNetworkCapabilities(network)
        val link = manager.getLinkProperties(network)
        val transport = when {
            capabilities == null -> "?"
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "wifi"
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "cell"
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "eth"
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN) -> "vpn"
            else -> "other"
        }
        val iface = link?.interfaceName ?: "?"
        val gateway = link?.routes
            ?.firstOrNull { it.isDefaultRoute }
            ?.gateway
            ?.hostAddress
            .orEmpty()
        val dns = link?.dnsServers?.firstOrNull()?.hostAddress.orEmpty()
        val legacy = "$transport|$iface|$gateway|$dns"
        return when (transport) {
            "wifi" -> wifiSsid()?.let { "wifi|$it" } ?: legacy
            "cell" -> cellOperator()?.let { "cell|$it" } ?: legacy
            else -> legacy
        }
    }

    /**
     * Raw profile data for the report uplink. Returns null if the active
     * network cannot be identified — the report still goes out with
     * "unknown" as the transport.
     */
    fun reportIdentity(): Report.Identity? {
        return try {
            val manager = app.getSystemService(ConnectivityManager::class.java) ?: return null
            val network = manager.activeNetwork ?: return null
            val capabilities = manager.getNetworkCapabilities(network) ?: return null
            val transport = when {
                capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "wifi"
                capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "cell"
                capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "eth"
                else -> "unknown"
            }
            when (transport) {
                "wifi" -> Report.Identity("wifi", wifiSsid(), null, null)
                "cell" -> {
                    val tm = app.getSystemService(Context.TELEPHONY_SERVICE) as? TelephonyManager
                    val carrier = tm?.networkOperatorName?.trim()?.takeIf { it.isNotBlank() }
                    val mccmnc = tm?.networkOperator?.trim()?.takeIf { it.isNotBlank() }
                    val country = tm?.networkCountryIso?.trim()?.uppercase()?.takeIf { it.isNotBlank() }
                    // Fall back to SIM carrier/country so reports keep a useful
                    // ISP signature even when the exit-geo lookup is blocked.
                    Report.Identity("cell", null, carrier, mccmnc, isp = carrier, country = country)
                }
                else -> Report.Identity(transport, null, null, null)
            }
        } catch (_: SecurityException) {
            null
        } catch (_: Exception) {
            null
        }
    }

    private fun wifiSsid(): String? {
        return try {
            val wifi = app.getSystemService(Context.WIFI_SERVICE) as? WifiManager ?: return null
            @Suppress("DEPRECATION")
            val raw = wifi.connectionInfo?.ssid ?: return null
            val clean = raw.trim().removeSurrounding("\"")
            clean.takeIf { it.isNotBlank() && it != "<unknown ssid>" }
        } catch (_: SecurityException) {
            null
        } catch (_: Exception) {
            null
        }
    }

    private fun cellOperator(): String? {
        return try {
            val tm = app.getSystemService(Context.TELEPHONY_SERVICE) as? TelephonyManager ?: return null
            val name = tm.networkOperatorName?.trim().orEmpty().takeIf { it.isNotBlank() }
            val code = tm.networkOperator?.trim().orEmpty().takeIf { it.isNotBlank() }
            when {
                name != null && code != null -> "$name|$code"
                code != null -> code
                name != null -> name
                else -> null
            }
        } catch (_: SecurityException) {
            null
        } catch (_: Exception) {
            null
        }
    }

    private fun decodeWinner(json: JSONObject): Winner = Winner(
        endpoint = json.getString("endpoint"),
        i1 = json.optString("i1"),
        i1Label = json.optString("i1Label"),
        jc = json.optInt("jc", DEFAULT_JC),
        jmin = json.optInt("jmin", DEFAULT_JMIN),
        jmax = json.optInt("jmax", DEFAULT_JMAX),
        mtu = json.optInt("mtu", DEFAULT_MTU),
        savedAt = json.optLong("savedAt"),
        networkKey = json.optString("networkKey"),
    )

    private fun encodeWinner(winner: Winner): JSONObject = JSONObject()
        .put("endpoint", winner.endpoint)
        .put("i1", winner.i1)
        .put("i1Label", winner.i1Label)
        .put("jc", winner.jc)
        .put("jmin", winner.jmin)
        .put("jmax", winner.jmax)
        .put("mtu", winner.mtu)
        .put("savedAt", winner.savedAt)
        .put("networkKey", winner.networkKey)

    fun deviceId(): String {
        synchronized(this) {
            val existing = prefs.getString(KEY_DEVICE_ID, null)
            if (existing != null) return existing
            val fresh = java.util.UUID.randomUUID().toString()
            // Synchronous write so a force-stop can't silently rotate the install ID.
            prefs.edit().putString(KEY_DEVICE_ID, fresh).commit()
            return fresh
        }
    }

    fun clearDeviceId() {
        prefs.edit().remove(KEY_DEVICE_ID).apply()
    }

    companion object {
        const val DEFAULT_JC = 6
        const val DEFAULT_JMIN = 10
        const val DEFAULT_JMAX = 50
        const val DEFAULT_MTU = 1280
        const val WINNER_TTL_MS = 24L * 60 * 60 * 1000
        const val SILENCE_MS = 6L * 60 * 60 * 1000

        private const val PREFS = "warpconnect"
        private const val KEY_ACCOUNT = "account"
        private const val KEY_WINNER = "winner"
        private const val KEY_WINNERS = "winners"
        private const val KEY_STATS = "statsRecommendation"
        private const val KEY_PROFILE_ASKED = "profilePermissionsAsked"
        private const val KEY_DEVICE_ID = "deviceId"
        private const val MAX_WINNERS = 8
    }
}
