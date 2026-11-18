package com.warpconnect.app

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

/**
 * Best-effort network info from the plain network, done before the tunnel
 * comes up. HTTPS-first chain (all of these return the caller's IP and
 * AS/org/country/city in one call):
 *   ipwho.is  ->  ifconfig.co/json  ->  api.ipapi.is
 * Any single failure is skipped; total failure returns null and the caller
 * falls back to the locally known carrier identity.
 */
object Isp {

    /** Carrier-only summary built from the locally known identity — used
     *  when every HTTPS lookup fails so the NETWORK line never renders blank. */
    fun describeIdentity(ident: Report.Identity?): String? {
        return buildString {
            when (ident?.transport) {
                "wifi" -> append(ident.ssid?.takeIf { it.isNotBlank() } ?: "Wi-Fi")
                "cell" -> {
                    append(ident.carrier?.takeIf { it.isNotBlank() } ?: "Cellular")
                    ident.mccmnc?.takeIf { it.isNotBlank() }?.let { append(" (").append(it).append(")") }
                }
                "eth" -> append("Ethernet")
                else -> if (ident != null) append(ident.transport) else append("unknown")
            }
            ident?.country?.takeIf { it.isNotBlank() }?.let { append(" | ").append(it) }
        }.takeIf { it.isNotBlank() && it != "unknown" }
    }

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
            val sep = " | "
            if (ip.isNotBlank()) append(ip)
            var needSep = ip.isNotBlank()
            if (asn.isNotBlank()) {
                if (needSep) append(sep)
                append(asn)
                if (asnOrg.isNotBlank()) append(" (").append(asnOrg).append(")")
                needSep = true
            }
            if (country.isNotBlank()) {
                if (needSep) append(sep)
                append(country)
                needSep = true
            }
            if (city.isNotBlank()) {
                if (needSep) append(sep)
                append(city)
            }
        }
    }

    suspend fun find(): NetInfo? = withContext(Dispatchers.IO) {
        fetchIpWhoIs() ?: fetchIfconfigCo() ?: fetchIpApiIs()
    }

    // ---------- providers ----------

    private fun fetchIpWhoIs(): NetInfo? = runCatching {
        val body = httpGet("https://ipwho.is/") ?: return null
        val root = JSONObject(body)
        if (root.has("success") && !root.optBoolean("success")) return null
        val conn = root.optJSONObject("connection")
        val asnNum = conn?.optInt("asn", -1)?.takeIf { it > 0 } ?: -1
        val asn = if (asnNum > 0) "AS$asnNum" else ""
        val org = conn?.optString("org")?.takeIf { it.isNotBlank() }
            ?: conn?.optString("isp")?.takeIf { it.isNotBlank() }
            ?: ""
        NetInfo(
            ip = root.optString("ip").ifBlank { "" },
            asn = asn,
            asnOrg = org,
            isp = org,
            country = root.optString("country_code").ifBlank { "" },
            city = root.optString("city").ifBlank { "" },
            timezone = root.optJSONObject("timezone")?.optString("id")?.ifBlank { "" } ?: "",
        )
    }.getOrNull()

    private fun fetchIfconfigCo(): NetInfo? = runCatching {
        val body = httpGet("https://ifconfig.co/json") ?: return null
        val root = JSONObject(body)
        NetInfo(
            ip = root.optString("ip").ifBlank { "" },
            asn = root.optString("asn").ifBlank { "" },
            asnOrg = root.optString("asn_org").ifBlank { "" },
            isp = root.optString("asn_org").ifBlank { "" },
            country = root.optString("country_iso").ifBlank { "" },
            city = "",
            timezone = root.optString("time_zone").ifBlank { "" },
        )
    }.getOrNull()

    private fun fetchIpApiIs(): NetInfo? = runCatching {
        val body = httpGet("https://api.ipapi.is/") ?: return null
        val root = JSONObject(body)
        val asnObj = root.optJSONObject("asn")
        val asn = asnObj?.optString("asn")?.takeIf { it.isNotBlank() } ?: ""
        val org = asnObj?.optString("org")?.takeIf { it.isNotBlank() }
            ?: root.optJSONObject("company")?.optString("name")?.takeIf { it.isNotBlank() }
            ?: ""
        val location = root.optJSONObject("location")
        NetInfo(
            ip = root.optString("ip").ifBlank { "" },
            asn = asn,
            asnOrg = org,
            isp = org,
            country = location?.optString("country_code")?.ifBlank { "" } ?: "",
            city = location?.optString("city")?.ifBlank { "" } ?: "",
            timezone = location?.optString("timezone")?.ifBlank { "" } ?: "",
        )
    }.getOrNull()

    // ---------- helpers ----------

    private fun httpGet(url: String, timeoutMs: Int = 5_000): String? = try {
        val conn = (URL(url).openConnection() as? HttpURLConnection) ?: return null
        try {
            conn.connectTimeout = timeoutMs
            conn.readTimeout = timeoutMs
            conn.requestMethod = "GET"
            conn.setRequestProperty("Accept", "application/json")
            if (conn.responseCode in 200..299) conn.inputStream.bufferedReader().readText() else null
        } finally {
            conn.disconnect()
        }
    } catch (_: Exception) {
        null
    }
}
