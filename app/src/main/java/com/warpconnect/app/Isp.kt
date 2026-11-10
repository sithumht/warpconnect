package com.warpconnect.app

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

/**
 * Best-effort network info from the plain network, done before the tunnel
 * comes up. One pre-flight: an IP echo via Cloudflare's trace, then a single
 * HTTPS lookup against api.ipapi.is (free, no key). Any failure yields null —
 * never blocks or leaks errors at the UI.
 */
object Isp {

    data class NetInfo(
        val ip: String,
        val asn: String,
        val asnOrg: String,
        val isp: String,
        val country: String,
        val city: String,
        val timezone: String,
    ) {
        /** e.g. "45.117.1.2 | AS9560 (MPT) | Myanmar | Yangon" */
        fun describe(): String = buildString {
            append(ip)
            if (asn.isNotBlank()) {
                append(" | ").append(asn)
                if (asnOrg.isNotBlank()) append(" (").append(asnOrg).append(")")
            }
            if (country.isNotBlank()) append(" | ").append(country)
            if (city.isNotBlank()) append(" | ").append(city)
        }
    }

    suspend fun find(): NetInfo? = withContext(Dispatchers.IO) {
        val ip = detectExternalIp() ?: return@withContext null
        lookup(ip)
    }

    private fun detectExternalIp(): String? {
        return try {
            val conn = (URL("https://1.1.1.1/cdn-cgi/trace").openConnection() as? HttpURLConnection) ?: return null
            conn.connectTimeout = 4_000
            conn.readTimeout = 4_000
            conn.requestMethod = "GET"
            val text = conn.inputStream.bufferedReader().readText()
            Regex("""(?m)^ip=([^\r\n]+)""").find(text)?.groupValues?.get(1)?.trim()?.takeIf { it.isNotBlank() }
        } catch (_: Exception) {
            null
        }
    }

    private fun lookup(ip: String): NetInfo? {
        return try {
            val url = URL("https://api.ipapi.is/?ip=$ip")
            val conn = (url.openConnection() as HttpURLConnection).apply {
                connectTimeout = 4_000
                readTimeout = 4_000
                requestMethod = "GET"
                setRequestProperty("Accept", "application/json")
            }
            if (conn.responseCode !in 200..299) return null
            val body = conn.inputStream.bufferedReader().readText()
            val root = JSONObject(body)

            // "asn": "AS132167 Nine Communications Company Limited"
            val asnRaw = root.optString("asn").trim()
            val (asnNumber, asnOrg) = splitAsn(asnRaw)

            NetInfo(
                ip = root.optString("ip", ip).ifBlank { ip },
                asn = asnNumber,
                asnOrg = asnOrg,
                isp = root.optString("company").ifBlank { asnOrg },
                country = root.optString("country").ifBlank { "" },
                city = root.optString("city").ifBlank { "" },
                timezone = root.optString("timezone").ifBlank { "" },
            )
        } catch (_: Exception) {
            null
        }
    }

    private fun splitAsn(raw: String): Pair<String, String> {
        if (raw.isBlank()) return "" to ""
        // Accept "AS132167 Org Name", "as132167,", or "AS132167".
        val idx = raw.indexOfFirst { it.isWhitespace() }
        if (idx < 0) return raw to ""
        return raw.substring(0, idx).trim() to raw.substring(idx + 1).trim()
    }
}
