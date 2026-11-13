package com.warpconnect.app

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update

enum class Phase { IDLE, PREPARING, CONNECTING, CONNECTED, ERROR }

data class WarpUiState(
    val phase: Phase = Phase.IDLE,
    val headline: String = "Ready",
    val detail: String = "Tap the button to connect through WARP.",
    val endpoint: String? = null,
    val connectedSince: Long = 0L,
    val log: List<String> = emptyList(),
    /** Set to a fresh, monotonically increasing id for each one-shot notice. */
    val toastId: Long = 0L,
    val toast: String? = null,
    /** Pre-tunnel network info (ASN, provider, country), shown on the home screen. */
    val netInfo: String? = null,
    /** Latest latency measurement (ms) through the tunnel, if a test was run. */
    val pingMs: Long? = null,
    /** Latest throughput measurement (kbps) through the tunnel, if a test was run. */
    val bandwidthKbps: Long? = null,
    /** Error for the most recent speed test, if any. */
    val speedTestError: String? = null,
) {
    val active: Boolean
        get() = phase == Phase.PREPARING || phase == Phase.CONNECTING || phase == Phase.CONNECTED
}

/**
 * Process-wide UI state. The service is the only writer; the activity only
 * renders and sends intents.
 */
object WarpHub {
    private val mutable = MutableStateFlow(WarpUiState())
    val state: StateFlow<WarpUiState> = mutable

    internal fun set(value: WarpUiState) {
        mutable.value = value
    }

    internal fun update(block: (WarpUiState) -> WarpUiState) {
        mutable.update(block)
    }

    internal fun log(line: String) {
        val clean = line.trim()
        if (clean.isEmpty()) return
        mutable.update { current ->
            val next = ArrayList<String>(current.log.size + 1)
            next.add(clean)
            next.addAll(current.log.take(29))
            current.copy(log = next)
        }
    }
}
