package com.warpconnect.app

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

/**
 * Best-effort ISP detection from the plain network, done before the tunnel
 * comes up. Two public, unauthenticated stages: an IP echo via Cloudflare's
 * trace endpoint, then an RDAP lookup against the RIR that owns the block.
 * Any failure just yields null — never blocks or leaks errors at the UI.
 */
object Isp {

    suspend fun find(): String? = withContext(Dispatchers.IO) {
        val ip = detectExternalIp() ?: return@withContext null
        lookupIsp(ip)
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

    private fun lookupIsp(ip: String): String? {
        val endpoints = listOf(
            "https://rdap.apnic.net/ip/$ip",
            "https://rdap.arin.net/registry/ip/$ip",
            "https://rdap.db.ripe.net/ip/$ip",
            "https://rdap.lacnic.net/rdap/ip/$ip",
        )
        for (endpoint in endpoints) {
            val body = try {
                val url = URL(endpoint)
                val conn = (url.openConnection() as HttpURLConnection).apply {
                    connectTimeout = 4_000
                    readTimeout = 4_000
                    requestMethod = "GET"
                    setRequestProperty("Accept", "application/rdap+json")
                }
                if (conn.responseCode !in 200..299) continue
                conn.inputStream.bufferedReader().readText()
            } catch (_: Exception) {
                continue
            }
            val org = parseOrg(body)
            if (!org.isNullOrBlank()) return org
        }
        return null
    }

    private fun parseOrg(body: String): String? {
        return try {
            val root = JSONObject(body)
            val entities = root.optJSONArray("entities")
            if (entities != null) {
                val preferred = mutableListOf<String>()
                val fallback = mutableListOf<String>()
                for (i in 0 until entities.length()) {
                    val entity = entities.optJSONObject(i) ?: continue
                    val roles = entity.optJSONArray("roles")?.let { arr ->
                        (0 until arr.length()).mapNotNull { arr.optString(it) }
                    } ?: emptyList()
                    val orgName = orgName(entity)
                    if (orgName.isNullOrBlank()) continue
                    if (roles.any { it.equals("registrant", true) }) {
                        preferred += orgName
                    } else if (roles.any { it.equals("administrative", true) }) {
                        fallback += orgName
                    } else {
                        fallback += orgName
                    }
                }
                preferred.firstOrNull() ?: fallback.firstOrNull()
            } else {
                root.optString("name").takeIf { it.isNotBlank() && it != "null" }
            }
        } catch (_: Exception) {
            null
        }
    }

    private fun orgName(entity: JSONObject): String? {
        val vcard = entity.optJSONArray("vcardArray") ?: return null
        if (vcard.length() < 2) return null
        val entries: JSONArray = vcard.optJSONArray(1) ?: return null
        var fn: String? = null
        var o: String? = null
        for (i in 0 until entries.length()) {
            val entry = entries.optJSONArray(i) ?: continue
            val key = entry.optString(0)
            val value = entry.opt(3)
            val text = when (value) {
                is String -> value
                is JSONArray -> value.optString(0).takeIf { it.isNotBlank() && it != "null" }
                else -> value?.toString()
            }?.trim()
            when (key.lowercase()) {
                "fn" -> fn = text?.takeIf { it.isNotBlank() }
                "o" -> o = text?.takeIf { it.isNotBlank() }
            }
        }
        return fn ?: o
    }
}
