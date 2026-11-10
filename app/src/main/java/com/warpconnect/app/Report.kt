package com.warpconnect.app

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.OutputStreamWriter
import java.net.HttpURLConnection
import java.net.URL

/**
 * Voluntary telemetry: on every verified connection the app reports the
 * working endpoint and the network profile that carried it. Used to find
 * which endpoint works on which ISP. Failures are ignored on purpose — this
 * is a best-effort signal and must never disturb the tunnel.
 */
object Report {
    private const val URL_STD = "https://warpconnect-stats.sithu-mhtvps.workers.dev/report"

    data class Identity(
        val transport: String,
        val ssid: String?,
        val carrier: String?,
        val mccmnc: String?,
    )

    /** Returns true when the report was accepted (2xx from the server). */
    suspend fun submit(identity: Identity?, endpoint: String): Boolean = withContext(Dispatchers.IO) {
        val payload = JSONObject()
            .put("endpoint", endpoint)
            .put("transport", identity?.transport ?: "unknown")
            .put("ssid", identity?.ssid ?: "")
            .put("carrier", identity?.carrier ?: "")
            .put("mccmnc", identity?.mccmnc ?: "")

        val body = payload.toString()
        var conn: HttpURLConnection? = null
        try {
            val url = URL(URL_STD)
            conn = (url.openConnection() as HttpURLConnection).apply {
                requestMethod = "POST"
                connectTimeout = 8_000
                readTimeout = 8_000
                doOutput = true
                setRequestProperty("Content-Type", "application/json")
                setRequestProperty("Content-Length", body.toByteArray(Charsets.UTF_8).size.toString())
            }
            OutputStreamWriter(conn.outputStream, Charsets.UTF_8).use { it.write(body) }
            conn.responseCode in 200..299
        } catch (_: Exception) {
            false
        } finally {
            conn?.disconnect()
        }
    }
}
