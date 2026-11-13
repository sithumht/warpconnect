package com.warpconnect.app

import android.content.Context
import android.os.Bundle
import com.google.firebase.analytics.FirebaseAnalytics

/**
 * Thin analytics wrapper so the service does not call Firebase directly.
 * Everything here is best-effort: any throw is swallowed, analytics must never
 * leak into tunnel or UI paths.
 */
object Analytics {
    private var instance: FirebaseAnalytics? = null

    fun init(context: Context) {
        if (instance != null) return
        runCatching {
            val fa = FirebaseAnalytics.getInstance(context)
            fa.setUserId(Store(context).deviceId())
            instance = fa
        }.onFailure {
            // keep a null inside the wrapper; logged nowhere by design
        }
    }

    private fun log(name: String, params: Bundle.() -> Unit = {}) {
        val fa = instance ?: return
        runCatching {
            val bundle = Bundle()
            bundle.params()
            fa.logEvent(name, bundle)
        }
    }

    fun trackLaunch() { log("app_launch") }
    fun trackConnectAttempt() { log("connect_attempt") }
    fun trackConnectSuccess(endpoint: String, handshakeMs: Long, fromNetwork: String) {
        log("connect_success") {
            putString("endpoint", endpoint)
            putLong("handshake_ms", handshakeMs)
            putString("network", fromNetwork)
        }
    }
    fun trackConnectFailure(reason: String) {
        log("connect_failure") {
            putString("reason", reason.take(80))
        }
    }
    fun trackDisconnect() { log("disconnect") }
    fun trackSpeedTest(pingMs: Long, kbps: Long) {
        log("speed_test") {
            putLong("ping_ms", pingMs)
            putLong("kbps", kbps)
        }
    }
    fun trackSpeedTestFailed(reason: String) {
        log("speed_test_failed") {
            putString("reason", reason.take(80))
        }
    }
}
