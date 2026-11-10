package com.warpconnect.app

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.VpnService
import android.os.Build
import android.os.PowerManager
import android.os.SystemClock
import androidx.core.app.NotificationCompat
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import org.json.JSONObject
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Owns the single WARP tunnel.
 *
 * Flow: account (registered once inside a bootstrap WARP tunnel) -> candidate
 * list (cached winner, then a scan, then a junk-profile deep search) -> for each
 * candidate: VpnService interface + amneziawg-go on its fd -> handshake check ->
 * data check -> monitor every 15 s and auto-heal if the endpoint is torn down.
 */
class WarpVpnService : VpnService() {

    private data class Candidate(
        val endpoint: String,
        val i1: String,
        val jc: Int,
        val jmin: Int,
        val jmax: Int,
        val mtu: Int,
    )

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private lateinit var store: Store
    private var work: Job? = null
    private var tunFd: Int = -1
    private var engineOwnsTun = false
    private var wakeLock: PowerManager.WakeLock? = null
    private val healTimes = ArrayDeque<Long>()

    @Volatile
    private var stopping = false

    private var networkCallback: ConnectivityManager.NetworkCallback? = null

    private fun registerNetworkWatchdog() {
        val cm = getSystemService(ConnectivityManager::class.java) ?: return
        val request = NetworkRequest.Builder()
            .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .build()
        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onLost(network: Network) {
                super.onLost(network)
                maybeStopForNoInternet()
            }

            override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) {
                super.onCapabilitiesChanged(network, caps)
                if (!caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)) {
                    maybeStopForNoInternet()
                }
            }
        }
        runCatching { cm.registerNetworkCallback(request, callback) }
            .onFailure { WarpHub.log("Network watchdog unavailable: ${it.message}") }
            .onSuccess { networkCallback = callback }
    }

    /**
     * True if any non-VPN network still reports the INTERNET capability.
     * The VPN itself does, so it is excluded — otherwise the check would
     * never notice an outage underneath the tunnel.
     */
    private fun hasInternetCapableNetwork(): Boolean {
        val cm = getSystemService(ConnectivityManager::class.java) ?: return true
        return try {
            cm.allNetworks.any { net ->
                runCatching {
                    val caps = cm.getNetworkCapabilities(net) ?: return@runCatching false
                    !caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN) &&
                        caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                }.getOrDefault(false)
            }
        } catch (_: SecurityException) {
            true // best effort: never tear the tunnel down on a security failure
        } catch (_: Exception) {
            true
        }
    }

    private fun maybeStopForNoInternet() {
        if (stopping) return
        if (!engineOwnsTun) return // nothing running to stop
        if (hasInternetCapableNetwork()) return
        WarpHub.log("Internet is offline — stopping WARP")
        shutdown(offline = true)
    }

    override fun onCreate() {
        super.onCreate()
        store = Store(this)
        createChannel()
        registerNetworkWatchdog()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_DISCONNECT -> {
                shutdown()
                return START_NOT_STICKY
            }
            ACTION_CONNECT -> {
                if (work?.isActive == true) return START_STICKY
                stopping = false
                startForegroundCompat("WARP Connect", "Preparing the tunnel")
                work = scope.launch { connect() }
                return START_STICKY
            }
        }
        // Restarted without a command: don't leave a headless notification.
        stopSelf()
        return START_NOT_STICKY
    }

    override fun onRevoke() {
        WarpHub.log("Android revoked the VPN permission")
        shutdown()
        super.onRevoke()
    }

    override fun onDestroy() {
        stopping = true
        networkCallback?.let { cb ->
            runCatching {
                getSystemService(ConnectivityManager::class.java)?.unregisterNetworkCallback(cb)
            }
            networkCallback = null
        }
        work?.cancel()
        Engine.cancel()
        // Blocking teardown: if the service dies while the device is still
        // attached, the TUN interface stays up and blackholes traffic.
        closeTunnel()
        releaseWakeLock()
        scope.cancel()
        super.onDestroy()
    }

    // ---------------------------------------------------------------- connect

    private suspend fun connect() {
        WarpHub.set(
            WarpUiState(
                phase = Phase.PREPARING,
                headline = "Preparing",
                detail = "Loading your WARP account",
                log = WarpHub.state.value.log,
            ),
        )
        WarpHub.log("Connect requested")

        try {
            acquireWakeLock()
            val networkKey = store.networkKey()
            val acct = ensureAccount(networkKey)
            WarpHub.log("Network profile: ${networkKey.take(60)}")

            // Retry recently verified endpoints for this exact network
            // signature first. This is nearly always seconds instead of a scan.
            val tried = HashSet<String>()
            val knownGood = store.recentWinners(networkKey).take(3)
            if (knownGood.isNotEmpty()) {
                WarpHub.log("Known-good endpoint profile for this network: ${knownGood.size} saved")
                for ((index, winner) in knownGood.withIndex()) {
                    val candidate = winner.toCandidate()
                    tried += candidate.endpoint
                    WarpHub.update {
                        it.copy(
                            phase = Phase.CONNECTING,
                            headline = "Connecting",
                            detail = "Retrying saved endpoint ${index + 1} of ${knownGood.size}",
                            endpoint = candidate.endpoint,
                        )
                    }
                    WarpHub.log("Trying saved endpoint ${candidate.endpoint} (${index + 1}/${knownGood.size})")
                    if (attempt(candidate, acct)) {
                        markConnected(candidate, networkKey)
                        monitor(networkKey, acct)
                        return
                    }
                    teardown()
                }
                WarpHub.log("No saved endpoint worked; scanning for a new one")
            }

            val candidates = buildPlan(acct, deep = false).filterNot { it.endpoint in tried }
            if (candidates.isEmpty()) {
                throw IllegalStateException("No working WARP endpoint was found on this network")
            }
            WarpHub.log("${candidates.size} candidate endpoint(s) ready")

            if (attemptAll(candidates, acct, networkKey, tried)) return

            // The scan called these endpoints durable but none survived the
            // handshake/data gates. That is exactly the "connects, then dies"
            // case, so escalate to junk-profile shaping before giving up.
            WarpHub.log("No scanned endpoint passed verification; retrying with a deep search")
            teardown()
            val deeper = buildPlan(acct, deep = true).filterNot { it.endpoint in tried }
            if (deeper.isEmpty()) {
                throw IllegalStateException("Every candidate endpoint failed verification")
            }
            WarpHub.log("${deeper.size} deep-search candidate endpoint(s) ready")
            if (attemptAll(deeper, acct, networkKey, tried)) return

            throw IllegalStateException("Every candidate endpoint failed verification")
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            if (stopping) return
            val message = e.message ?: "The tunnel could not be established"
            WarpHub.log("Failed: $message")
            WarpHub.set(
                WarpUiState(
                    phase = Phase.ERROR,
                    headline = "Not connected",
                    detail = message,
                    log = WarpHub.state.value.log,
                ),
            )
            notify("Not connected", message)
            teardown()
            releaseWakeLock()
            stopForegroundCompat()
            stopSelf()
        }
    }

    /**
     * Registration happens exactly once, inside a WARP tunnel built from the
     * embedded bootstrap keys. There is no direct API call and no relay in this
     * path, so a successful account also proves that endpoint carries data.
     */
    private suspend fun ensureAccount(networkKey: String): JSONObject {
        store.accountJson?.let { raw ->
            val parsed = runCatching { JSONObject(raw) }.getOrNull()
            if (parsed != null &&
                parsed.optString("private_key").isNotBlank() &&
                parsed.optString("peer_public_key").isNotBlank() &&
                parsed.optString("ipv4").isNotBlank()
            ) {
                return parsed
            }
        }

        WarpHub.update {
            it.copy(
                headline = "Registering",
                detail = "Registering a WARP account",
            )
        }
        WarpHub.log("No account yet: registering (direct, relay, then bootstrap tunnel)")
        val registration = engineTimeout(REGISTER_TIMEOUT_MS) {
            Engine.registerThroughTunnel(baseOptions()) { _, _, _, message ->
                if (message.isNotBlank() && !stopping) {
                    WarpHub.update { it.copy(detail = message) }
                }
            }
        } ?: throw IllegalStateException(
            "Could not register an account on this network (direct, relay, and bootstrap tunnel all failed)",
        )
        store.accountJson = registration.accountJson
        if (registration.endpoint.isNotBlank()) {
            store.saveWinner(
                Winner(
                    endpoint = registration.endpoint,
                    i1 = registration.i1,
                    i1Label = registration.i1Label,
                    jc = registration.jc,
                    jmin = registration.jmin,
                    jmax = registration.jmax,
                    mtu = registration.mtu,
                    savedAt = System.currentTimeMillis(),
                    networkKey = networkKey,
                ),
            )
            WarpHub.log("Account registered through ${registration.endpoint}")
        } else {
            // Direct/relay registrations have no bootstrap endpoint to pin to;
            // normal scan will find endpoints for this network.
            WarpHub.log("Account registered")
        }
        return JSONObject(registration.accountJson)
    }

    // ------------------------------------------------------------ candidate set

    /**
     * Tries every candidate in order. Returns true when one connected (in which
     * case `monitor` ran to completion), false if they all failed verification.
     */
    private suspend fun attemptAll(
        candidates: List<Candidate>,
        acct: JSONObject,
        networkKey: String,
        tried: MutableSet<String>,
    ): Boolean {
        for ((index, candidate) in candidates.withIndex()) {
            if (stopping) return true
            tried += candidate.endpoint
            WarpHub.update {
                it.copy(
                    phase = Phase.CONNECTING,
                    headline = "Connecting",
                    detail = "Attempt ${index + 1} of ${candidates.size}: ${candidate.endpoint}",
                    endpoint = candidate.endpoint,
                )
            }
            if (attempt(candidate, acct)) {
                markConnected(candidate, networkKey)
                monitor(networkKey, acct)
                return true
            }
            WarpHub.log("${candidate.endpoint} did not pass verification")
        }
        return false
    }

    private suspend fun buildPlan(
        acct: JSONObject,
        deep: Boolean,
        allowDeep: Boolean = true,
    ): List<Candidate> {
        val plan = ArrayList<Candidate>()

        WarpHub.update {
            it.copy(
                phase = Phase.PREPARING,
                headline = "Scanning",
                detail = "Looking for a WARP endpoint that passes real traffic",
                endpoint = null,
            )
        }
        WarpHub.log("Scanning WARP endpoints with automatic packet profiles")
        var report: ScanReport? = null
        try {
            report = Engine.scan(acct.toString(), baseOptions()) { _, done, total, message ->
                if (total > 0 && !stopping) {
                    val suffix = if (message.isNotBlank()) " · $message" else ""
                    WarpHub.update { it.copy(detail = "$done/$total probed$suffix") }
                }
            }
            addScanCandidates(plan, report)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            WarpHub.log("Scan failed: ${e.message}")
        }

        if (allowDeep && (deep || report == null || !hasDurable(report))) {
            WarpHub.update {
                it.copy(
                    headline = "Deep search",
                    detail = "Standard scan was not durable. Testing junk profiles (up to 3 minutes)",
                )
            }
            WarpHub.log("Standard scan found nothing durable; searching junk profiles")
            val junk = try {
                engineTimeout(180_000) {
                    Engine.findJunk(acct.toString(), baseOptions()) { _, _, _, message ->
                        if (message.isNotBlank() && !stopping) {
                            WarpHub.update { it.copy(detail = message) }
                        }
                    }
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                WarpHub.log("Deep search failed: ${e.message}")
                null
            }
            if (junk != null) {
                WarpHub.log(
                    "Deep search chose jc=${junk.junkCount} jmin=${junk.junkMin} jmax=${junk.junkMax}",
                )
                val pinned = baseOptions().apply {
                    put("awgAutoI1", false)
                    if (junk.i1.isNotBlank()) put("awgI1", junk.i1)
                    put("awgJunkCount", junk.junkCount)
                    put("awgJunkMin", junk.junkMin)
                    put("awgJunkMax", junk.junkMax)
                }
                WarpHub.update {
                    it.copy(headline = "Scanning", detail = "Rescanning with the winning junk profile")
                }
                try {
                    report = Engine.scan(acct.toString(), pinned) { _, done, total, _ ->
                        if (total > 0 && !stopping) {
                            WarpHub.update { it.copy(detail = "$done/$total probed with the winning junk profile") }
                        }
                    }
                    // Endpoints that survived the pinned rescan use the profile
                    // that actually got through, so try them before the ones
                    // from the default-profile scan.
                    val pinnedPlan = ArrayList<Candidate>()
                    addScanCandidates(pinnedPlan, report, junk.junkCount, junk.junkMin, junk.junkMax)
                    plan.addAll(0, pinnedPlan)
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    WarpHub.log("Scan with the winning junk profile failed: ${e.message}")
                }
            }
        }

        return plan.distinctBy { it.endpoint }.take(MAX_CANDIDATES)
    }

    /**
     * A reduced-effort scan for auto-heal: one sample per subnet, more parallel
     * workers, fewer pings, hard deadline. Heal prioritises being back online
     * over finding the theoretically optimal endpoint.
     */
    private suspend fun quickHealPlan(acct: JSONObject): List<Candidate> {
        val plan = ArrayList<Candidate>()
        WarpHub.update {
            it.copy(
                phase = Phase.CONNECTING,
                headline = "Reconnecting",
                detail = "Quick scan for a working endpoint",
                endpoint = null,
            )
        }
        WarpHub.log("Heal: running a quick scan")
        val fastOptions = baseOptions().apply {
            put("jobs", 14)
            put("samplePerSubnet", 1)
            put("tunnelPingCount", 6)
        }
        try {
            engineTimeout(QUICK_HEAL_TIMEOUT_MS) {
                val report = Engine.scan(acct.toString(), fastOptions) { _, done, total, message ->
                    if (total > 0 && !stopping) {
                        val suffix = if (message.isNotBlank()) " · $message" else ""
                        WarpHub.update { it.copy(detail = "$done/$total probed$suffix") }
                    }
                }
                addScanCandidates(plan, report)
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            WarpHub.log("Heal quick scan failed: ${e.message}")
        }
        return plan.distinctBy { it.endpoint }.take(MAX_CANDIDATES)
    }

    private fun markConnected(candidate: Candidate, networkKey: String, notice: String? = null) {
        store.saveWinner(
            Winner(
                endpoint = candidate.endpoint,
                i1 = candidate.i1,
                i1Label = "",
                jc = candidate.jc,
                jmin = candidate.jmin,
                jmax = candidate.jmax,
                mtu = candidate.mtu,
                savedAt = System.currentTimeMillis(),
                networkKey = networkKey,
            ),
        )
        val previousToastId = WarpHub.state.value.toastId
        WarpHub.set(
            WarpUiState(
                phase = Phase.CONNECTED,
                headline = "Connected",
                detail = notice ?: "WARP is carrying your traffic.",
                endpoint = candidate.endpoint,
                connectedSince = System.currentTimeMillis(),
                log = WarpHub.state.value.log,
                toastId = if (notice != null) previousToastId + 1 else previousToastId,
                toast = notice,
            ),
        )
        WarpHub.log("Connected to ${candidate.endpoint}")
        if (notice != null) {
            notify("Reconnected", notice)
        } else {
            notify("Connected", candidate.endpoint)
        }
    }

    private fun addScanCandidates(
        plan: MutableList<Candidate>,
        report: ScanReport,
        jc: Int = Store.DEFAULT_JC,
        jmin: Int = Store.DEFAULT_JMIN,
        jmax: Int = Store.DEFAULT_JMAX,
    ) {
        val winners = report.candidates(MAX_CANDIDATES)
        for (result in winners) {
            plan += Candidate(
                endpoint = result.endpoint,
                i1 = report.awgI1,
                jc = jc,
                jmin = jmin,
                jmax = jmax,
                mtu = Store.DEFAULT_MTU,
            )
        }
        if (winners.isNotEmpty()) {
            val label = winners.first().label
            WarpHub.log(
                "Scan picked ${winners.size} endpoint(s)" + if (label.isNotBlank()) " near $label" else "",
            )
        }
    }

    private fun hasDurable(report: ScanReport): Boolean =
        report.results.any { it.working && it.durable }

    // ---------------------------------------------------------------- one try

    private fun Winner.toCandidate() = Candidate(
        endpoint = endpoint,
        i1 = i1,
        jc = jc,
        jmin = jmin,
        jmax = jmax,
        mtu = mtu,
    )

    private suspend fun attempt(candidate: Candidate, acct: JSONObject): Boolean {
        val ipv4 = acct.optString("ipv4").substringBefore('/')
        val ipv6 = acct.optString("ipv6").substringBefore('/')
        val privateKey = acct.optString("private_key")
        val peerKey = acct.optString("peer_public_key")
        if (ipv4.isBlank() || privateKey.isBlank() || peerKey.isBlank()) {
            WarpHub.log("The saved account is incomplete")
            return false
        }

        // Build the engine spec before any interface exists, so a bad account
        // can never leave a routed-but-unread TUN behind.
        val spec = JSONObject()
            .put("privateKey", privateKey)
            .put("peerPublicKey", peerKey)
            .put("endpoint", candidate.endpoint)
            .put("keepalive", 25)
        if (candidate.i1.isNotBlank()) spec.put("i1", candidate.i1)
        if (candidate.jc > 0) spec.put("jc", candidate.jc)
        if (candidate.jmin > 0) spec.put("jmin", candidate.jmin)
        if (candidate.jmax > 0) spec.put("jmax", candidate.jmax)

        WarpHub.update { it.copy(detail = "Handshaking with ${candidate.endpoint}") }
        val pfd = try {
            Builder()
                .setSession("WARP Connect")
                .setMtu(candidate.mtu.coerceIn(1280, 1500))
                .addAddress(ipv4, 32)
                .apply {
                    if (ipv6.isNotBlank()) {
                        addAddress(ipv6, 128)
                        addRoute("::", 0)
                    }
                }
                .addDnsServer("1.1.1.1")
                .addDnsServer("1.0.0.1")
                .addRoute("0.0.0.0", 0)
                .setBlocking(false)
                .establish()
        } catch (e: Exception) {
            WarpHub.log("Android refused the VPN interface: ${e.message}")
            null
        }
        if (pfd == null) return false

        // Transfer ownership of the fd to amneziawg-go. From here on only Go
        // closes it (TunnelDown, or TunnelUp's own error path); touching the raw
        // number again could hit a recycled descriptor.
        val fd = try {
            pfd.detachFd()
        } catch (e: Exception) {
            WarpHub.log("Could not hand the TUN descriptor to the engine: ${e.message}")
            runCatching { pfd.close() }
            return false
        }
        tunFd = fd
        engineOwnsTun = true

        var passed = false
        try {
            try {
                // Non-cancellable: if this were interrupted between detach and
                // the engine taking the fd, nothing would ever close it and the
                // interface would stay routed. Once it returns, `closeTunnel`
                // owns teardown either way.
                withContext(NonCancellable) {
                    Engine.tunnelUp(spec, fd)
                    protectDeviceSockets()
                }
            } catch (e: Exception) {
                WarpHub.log("Tunnel start failed: ${e.message}")
                // Go already closed the descriptor on its error path.
                tunFd = -1
                engineOwnsTun = false
                return false
            }

            WarpHub.update { it.copy(detail = "Waiting for the WARP handshake") }
            if (!awaitHandshake()) {
                WarpHub.log("No handshake from ${candidate.endpoint}")
                return false
            }

            WarpHub.update { it.copy(detail = "Verifying that data flows through the tunnel") }
            passed = verifyData(candidate)
            return passed
        } finally {
            // Whatever happens (including cancellation), never leave a half
            // verified tunnel routed over all traffic.
            if (!passed) closeTunnel()
        }
    }

    private suspend fun awaitHandshake(): Boolean {
        val deadline = SystemClock.elapsedRealtime() + HANDSHAKE_TIMEOUT_MS
        while (SystemClock.elapsedRealtime() < deadline) {
            delay(250)
            val stats = runCatching { Engine.tunnelStats() }.getOrNull() ?: continue
            if (stats.up && stats.handshakeTime > 0) return true
        }
        return false
    }

    /**
     * A successful TCP connect from this process has to cross the tunnel,
     * because the app is not excluded from its own VPN. The rx counter delta is
     * the cross-check that the peer actually answered inside the tunnel.
     */
    private suspend fun verifyData(candidate: Candidate): Boolean {
        val before = runCatching { Engine.tunnelStats() }.getOrNull()
        var reachable = probeTcp()
        if (!reachable) {
            delay(800)
            reachable = probeTcp()
        }
        delay(500)
        val after = runCatching { Engine.tunnelStats() }.getOrNull()

        if (after == null || !after.up || after.handshakeTime <= 0) {
            WarpHub.log("The tunnel stopped reporting a handshake during the data check")
            return false
        }
        val rxDelta = after.rxBytes - (before?.rxBytes ?: 0)

        if (reachable && rxDelta >= 64) {
            WarpHub.log("Data verified on ${candidate.endpoint} (+$rxDelta bytes received)")
            return true
        }
        if (reachable) {
            WarpHub.log(
                "Probe succeeded but the counters did not move; keeping the scan-verified endpoint",
            )
            return true
        }
        if (rxDelta >= 512) {
            WarpHub.log("Probe was inconclusive, but the tunnel is receiving data (+$rxDelta bytes)")
            return true
        }
        WarpHub.log("Data check failed on ${candidate.endpoint} (rx +$rxDelta bytes)")
        return false
    }

    private suspend fun probeTcp(): Boolean = withContext(Dispatchers.IO) {
        try {
            Socket().use { socket ->
                socket.connect(InetSocketAddress("1.1.1.1", 443), 4500)
            }
            true
        } catch (_: Exception) {
            false
        }
    }

    private suspend fun protectDeviceSockets() {
        val fds = Engine.tunnelSocketFDs()
        for (fd in intArrayOf(fds.v4, fds.v6)) {
            if (fd <= 0) continue
            if (!runCatching { protect(fd) }.getOrDefault(false)) {
                WarpHub.log("Could not protect tunnel socket $fd from the VPN")
            }
        }
    }

    // -------------------------------------------------------------- monitoring

    private suspend fun monitor(networkKey: String, acct: JSONObject) {
        var failures = 0
        while (!stopping) {
            delay(MONITOR_INTERVAL_MS)
            if (stopping) return
            // Backstop for the network watchdog: if no non-VPN network still
            // reports internet connectivity, stop the tunnel instead of
            // probing (and healing) against a dead network.
            if (!hasInternetCapableNetwork()) {
                WarpHub.log("Internet is offline — stopping WARP (monitor backstop)")
                releaseWakeLock()
                shutdown(offline = true)
                return
            }
            // Hold the CPU only for the check itself. A foreground VPN device
            // is woken by packets; keeping the device awake 24/7 just to run a
            // 15 s probe would drain the battery for no benefit.
            acquireWakeLock(WAKE_LOCK_TICK_MS)

            val stats = runCatching { Engine.tunnelStats() }.getOrNull()
            val alive = stats != null &&
                stats.up &&
                stats.handshakeTime > 0 &&
                stats.handshakeAgeMs < 300_000
            val reachable = if (alive) probeTcp() else false
            if (alive && reachable) {
                failures = 0
                releaseWakeLock()
                continue
            }

            failures++
            WarpHub.log("Health check failed ($failures/$HEALTH_FAILURES_BEFORE_HEAL)")
            if (failures < HEALTH_FAILURES_BEFORE_HEAL) {
                releaseWakeLock()
                continue
            }

            // Healing can take minutes (scan + deep search): keep the CPU awake
            // until it is done.
            acquireWakeLock()

            WarpHub.update {
                it.copy(
                    phase = Phase.CONNECTING,
                    headline = "Healing",
                    detail = "The tunnel stopped passing traffic. Reconnecting.",
                )
            }
            WarpHub.log("Tunnel torn down; starting auto-heal")
            notify("Reconnecting", "The tunnel dropped. Retrying.")
            teardown()

            val healed = heal(networkKey, acct)
            if (healed == null) {
                WarpHub.set(
                    WarpUiState(
                        phase = Phase.ERROR,
                        headline = "Not connected",
                        detail = "The tunnel dropped and could not be restored.",
                        log = WarpHub.state.value.log,
                    ),
                )
                notify("Not connected", "The tunnel dropped and could not be restored.")
                releaseWakeLock()
                stopForegroundCompat()
                stopSelf()
                return
            }

            markConnected(healed, networkKey, notice = "Reconnected on ${healed.endpoint}")
            failures = 0
        }
    }

    private suspend fun heal(networkKey: String, acct: JSONObject): Candidate? {
        val now = System.currentTimeMillis()
        while (healTimes.isNotEmpty() && now - healTimes.first() > HEAL_WINDOW_MS) {
            healTimes.removeFirst()
        }
        if (healTimes.size >= HEAL_MAX_PER_WINDOW) {
            WarpHub.log("Auto-heal limit reached ($HEAL_MAX_PER_WINDOW per hour)")
            return null
        }
        healTimes.addLast(now)
        val backoff = 2_000L shl (healTimes.size - 1)
        WarpHub.log("Auto-heal in ${backoff / 1000}s (${healTimes.size}/$HEAL_MAX_PER_WINDOW this hour)")
        delay(backoff)
        if (stopping) return null

        // The tunnel usually drops because the endpoint/network flapped, not
        // because the endpoint disappeared. Retrying recently-verified endpoints
        // for this network costs seconds; a full scan is what makes heal slow.
        val knownGood = store.recentWinners(networkKey)
        if (knownGood.isNotEmpty()) {
            WarpHub.log("Heal: ${knownGood.size} known-good endpoint(s) for this network")
            for ((index, winner) in knownGood.withIndex()) {
                if (stopping) return null
                val candidate = winner.toCandidate()
                WarpHub.update {
                    it.copy(
                        phase = Phase.CONNECTING,
                        headline = "Reconnecting",
                        detail = "Healing: endpoint ${index + 1} of ${knownGood.size}",
                        endpoint = candidate.endpoint,
                    )
                }
                WarpHub.log("Heal: retrying ${candidate.endpoint} (${index + 1}/${knownGood.size})")
                if (attempt(candidate, acct)) {
                    WarpHub.log("Healed on ${candidate.endpoint}")
                    return candidate
                }
                teardown()
            }
            WarpHub.log("Heal: no known-good endpoint recovered; scanning")
        }

        // Fast path: single sample per subnet with a hard deadline.
        val quick = try {
            quickHealPlan(acct)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            WarpHub.log("Heal quick scan failed: ${e.message}")
            emptyList()
        }
        val quickResult = tryHealCandidates(quick, acct)
        if (quickResult != null) return quickResult

        // Last resort: full scan (still without the 3-minute junk deep-search).
        WarpHub.log("Heal: quick path failed; running a full scan")
        return try {
            tryHealCandidates(buildPlan(acct, deep = false, allowDeep = false), acct)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            WarpHub.log("Heal scan failed: ${e.message}")
            null
        }
    }

    private suspend fun tryHealCandidates(
        candidates: List<Candidate>,
        acct: JSONObject,
    ): Candidate? {
        for ((index, candidate) in candidates.withIndex()) {
            if (stopping) return null
            WarpHub.update {
                it.copy(
                    phase = Phase.CONNECTING,
                    headline = "Reconnecting",
                    detail = "Healing: attempt ${index + 1} of ${candidates.size}",
                    endpoint = candidate.endpoint,
                )
            }
            if (attempt(candidate, acct)) {
                WarpHub.log("Healed on ${candidate.endpoint}")
                return candidate
            }
            teardown()
        }
        return null
    }

    // ------------------------------------------------------------------ utils

    private fun baseOptions(): JSONObject = JSONObject()
        .put("protocol", "awg")
        .put("awgAutoI1", true)
        .put("jobs", 6)
        .put("samplePerSubnet", 3)
        .put("timeoutSec", 2)
        .put("tunnelPingCount", 10)
        .put("mtu", Store.DEFAULT_MTU)

    /**
     * Runs an engine operation with a hard deadline. The engine's API is a
     * blocking call, so the only way to stop it is `Mobileapi.cancel()`; the
     * coroutine can only observe cancellation once the Go call returns.
     */
    private suspend fun <T> engineTimeout(millis: Long, block: suspend () -> T): T? {
        val timedOut = AtomicBoolean(false)
        val job = scope.async { block() }
        val timer = scope.launch {
            delay(millis)
            timedOut.set(true)
            Engine.cancel()
        }
        return try {
            job.await()
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            if (timedOut.get()) null else throw e
        } finally {
            timer.cancel()
            if (!job.isCompleted) {
                Engine.cancel()
                withContext(NonCancellable) {
                    withTimeoutOrNull(5_000) { job.join() }
                }
            }
        }
    }

    private suspend fun teardown() {
        closeTunnel()
    }

    /**
     * Closes the tunnel exactly once. The descriptor belongs to amneziawg-go
     * after `tunnelUp` is called, so teardown always goes through the engine
     * instead of closing the raw number.
     */
    private fun closeTunnel() {
        tunFd = -1
        if (engineOwnsTun) {
            engineOwnsTun = false
            Engine.tunnelDownNow()
        }
    }

    private fun shutdown(offline: Boolean = false) {
        stopping = true
        work?.cancel()
        Engine.cancel()
        scope.launch {
            withContext(NonCancellable) {
                teardown()
                releaseWakeLock()
                WarpHub.log(if (offline) "Disconnected: internet offline" else "Disconnected")
                WarpHub.set(
                    WarpUiState(
                        phase = Phase.IDLE,
                        headline = "Ready",
                        detail = if (offline) {
                            "Internet is offline — the VPN has been turned off. Tap CONNECT to reconnect."
                        } else {
                            "Tap the button to connect through WARP."
                        },
                        log = WarpHub.state.value.log,
                    ),
                )
                stopForegroundCompat()
                stopSelf()
            }
        }
    }

    private fun acquireWakeLock(timeoutMs: Long = WAKE_LOCK_WORK_MS) {
        val power = getSystemService(PowerManager::class.java) ?: return
        val lock = wakeLock ?: power
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "warpconnect:tunnel")
            .apply { setReferenceCounted(false) }
            .also { wakeLock = it }
        // Timeouts are mandatory in practice: a scan may take minutes, and a
        // monitor tick only needs seconds, so a stuck process cannot drain the
        // battery forever.
        runCatching { lock.acquire(timeoutMs) }
    }

    private fun releaseWakeLock() {
        runCatching {
            if (wakeLock?.isHeld == true) wakeLock?.release()
        }
    }

    // ----------------------------------------------------------- notification

    private fun createChannel() {
        val manager = getSystemService(NotificationManager::class.java) ?: return
        val channel = NotificationChannel(
            CHANNEL_ID,
            "Tunnel",
            NotificationManager.IMPORTANCE_LOW,
        ).apply {
            description = "WARP tunnel status"
            setShowBadge(false)
        }
        manager.createNotificationChannel(channel)
    }

    private fun buildNotification(title: String, text: String): Notification {
        val open = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val stop = PendingIntent.getService(
            this,
            1,
            Intent(this, WarpVpnService::class.java).setAction(ACTION_DISCONNECT),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .setContentText(text)
            .setOnlyAlertOnce(true)
            .setOngoing(true)
            .setContentIntent(open)
            .addAction(0, "Disconnect", stop)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .build()
    }

    private fun startForegroundCompat(title: String, text: String) {
        val notification = buildNotification(title, text)
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startForeground(NOTIFICATION_ID, notification)
            return
        }
        // `specialUse` only needs the normal permission declared in the
        // manifest. VPN apps have no dedicated foreground type, and
        // `systemExempted` is reserved for alarm-clock exemptions.
        for (type in FOREGROUND_TYPES) {
            try {
                startForeground(NOTIFICATION_ID, notification, type)
                return
            } catch (_: Exception) {
                // Fall back to whatever the manifest declares.
            }
        }
        startForeground(NOTIFICATION_ID, notification)
    }

    private fun notify(title: String, text: String) {
        val manager = getSystemService(NotificationManager::class.java) ?: return
        runCatching { manager.notify(NOTIFICATION_ID, buildNotification(title, text)) }
    }

    private fun stopForegroundCompat() {
        stopForeground(STOP_FOREGROUND_REMOVE)
    }

    companion object {
        const val ACTION_CONNECT = "com.warpconnect.app.action.CONNECT"
        const val ACTION_DISCONNECT = "com.warpconnect.app.action.DISCONNECT"

        private const val CHANNEL_ID = "warp_tunnel"
        private const val NOTIFICATION_ID = 1001
        private const val HANDSHAKE_TIMEOUT_MS = 15_000L
        private const val MONITOR_INTERVAL_MS = 15_000L
        private const val HEALTH_FAILURES_BEFORE_HEAL = 2
        private const val MAX_CANDIDATES = 4
        private const val HEAL_WINDOW_MS = 60L * 60 * 1000
        private const val HEAL_MAX_PER_WINDOW = 3
        private const val REGISTER_TIMEOUT_MS = 300_000L
        private const val QUICK_HEAL_TIMEOUT_MS = 45_000L
        private const val WAKE_LOCK_WORK_MS = 10L * 60 * 1000
        private const val WAKE_LOCK_TICK_MS = 60L * 1000

        private val FOREGROUND_TYPES = intArrayOf(
            ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE,
        )
    }
}
