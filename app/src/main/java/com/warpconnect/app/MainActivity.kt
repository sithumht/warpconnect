package com.warpconnect.app

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.State
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import android.widget.Toast
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle

class MainActivity : ComponentActivity() {

    private var pendingStart by mutableStateOf(false)

    private val consent = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        val wantsStart = pendingStart
        pendingStart = false
        if (!wantsStart) return@registerForActivityResult
        if (result.resultCode == Activity.RESULT_OK) {
            startTunnel()
        } else {
            WarpHub.log("VPN permission was denied")
        }
    }

    private val notifications = registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            WarpTheme {
                val context = LocalContext.current
                val state by WarpHub.state.collectAsStateWithLifecycle()
                LaunchedEffect(state.toastId) {
                    val message = state.toast ?: return@LaunchedEffect
                    if (state.toastId == 0L) return@LaunchedEffect
                    Toast.makeText(context, message, Toast.LENGTH_LONG).show()
                    WarpHub.update { it.copy(toast = null) }
                }
                WarpScreen(
                    state = state,
                    onToggle = {
                        if (state.active) disconnect() else requestConnect()
                    },
                )
            }
        }
    }

    private fun requestConnect() {
        val consentIntent = runCatching { VpnService.prepare(this) }.getOrNull()
        if (consentIntent != null) {
            pendingStart = true
            consent.launch(consentIntent)
        } else {
            startTunnel()
        }
    }

    private fun startTunnel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            notifications.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
        val intent = Intent(this, WarpVpnService::class.java).setAction(WarpVpnService.ACTION_CONNECT)
        ContextCompat.startForegroundService(this, intent)
    }

    private fun disconnect() {
        startService(Intent(this, WarpVpnService::class.java).setAction(WarpVpnService.ACTION_DISCONNECT))
    }
}

@Composable
private fun WarpScreen(state: WarpUiState, onToggle: () -> Unit) {
    val accent = when (state.phase) {
        Phase.CONNECTED -> Color(0xFF34D399)
        Phase.ERROR -> Color(0xFFF87171)
        Phase.PREPARING, Phase.CONNECTING -> Color(0xFFFBBF24)
        Phase.IDLE -> Color(0xFF38BDF8)
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(
                Brush.verticalGradient(
                    listOf(Color(0xFF05070F), Color(0xFF0A1A30), Color(0xFF05070F)),
                ),
            ),
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .windowInsetsPadding(WindowInsets.safeDrawing)
                .padding(horizontal = 24.dp, vertical = 16.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text(
                text = "WARP CONNECT",
                fontSize = 12.sp,
                letterSpacing = 4.sp,
                color = Color(0xFF7C8BA4),
                fontWeight = FontWeight.SemiBold,
            )
            Spacer(Modifier.height(26.dp))
            Text(
                text = state.headline,
                fontSize = 30.sp,
                fontWeight = FontWeight.Bold,
                color = Color(0xFFF4F8FF),
                textAlign = TextAlign.Center,
            )
            Spacer(Modifier.height(8.dp))
            Text(
                text = state.detail,
                fontSize = 14.sp,
                color = Color(0xFF9FB0C9),
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(horizontal = 4.dp),
            )
            Spacer(Modifier.height(26.dp))
            ConnectButton(state = state, accent = accent, onClick = onToggle)
            Spacer(Modifier.height(22.dp))

            state.endpoint?.takeIf { it.isNotBlank() }?.let { endpoint ->
                Text(
                    text = "ENDPOINT",
                    fontSize = 10.sp,
                    letterSpacing = 2.sp,
                    color = Color(0xFF6B7A93),
                )
                Spacer(Modifier.height(4.dp))
                Text(
                    text = endpoint,
                    fontSize = 13.sp,
                    color = Color(0xFFBFD0E6),
                    fontFamily = FontFamily.Monospace,
                    textAlign = TextAlign.Center,
                )
                Spacer(Modifier.height(18.dp))
            }

            Surface(
                color = Color(0xFF0B1220),
                shape = RoundedCornerShape(18.dp),
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f),
            ) {
                Column(modifier = Modifier.padding(16.dp)) {
                    Text(
                        text = "ACTIVITY",
                        fontSize = 10.sp,
                        letterSpacing = 2.sp,
                        color = Color(0xFF6B7A93),
                    )
                    Spacer(Modifier.height(10.dp))
                    if (state.log.isEmpty()) {
                        Text(
                            text = "Nothing yet.",
                            fontSize = 12.sp,
                            color = Color(0xFF5D6B80),
                        )
                    } else {
                        Column(modifier = Modifier.verticalScroll(rememberScrollState())) {
                            state.log.forEach { line ->
                                Text(
                                    text = "• $line",
                                    fontSize = 12.sp,
                                    color = Color(0xFF92A4BE),
                                    fontFamily = FontFamily.Monospace,
                                    modifier = Modifier.padding(vertical = 3.dp),
                                )
                            }
                        }
                    }
                }
            }

            Spacer(Modifier.height(10.dp))
            Text(
                text = "Developed by Sithu",
                fontSize = 11.sp,
                letterSpacing = 1.sp,
                color = Color(0xFF5D6B80),
                textAlign = TextAlign.Center,
                modifier = Modifier.fillMaxWidth(),
            )
        }
    }
}

@Composable
private fun LightPulse(color: Color, phase: State<Float>) {
    Box(
        modifier = Modifier
            .size(260.dp)
            .graphicsLayer {
                val progress = phase.value % 1f
                val scale = 1f + 0.35f * progress
                scaleX = scale
                scaleY = scale
                alpha = 0.55f * (1f - 0.75f * progress)
            }
            .clip(CircleShape)
            .background(
                Brush.radialGradient(
                    0f to Color.Transparent,
                    0.55f to Color.Transparent,
                    0.85f to color,
                    1f to Color.Transparent,
                ),
            ),
    )
}

@Composable
private fun ConnectButton(state: WarpUiState, accent: Color, onClick: () -> Unit) {
    val label = when (state.phase) {
        Phase.CONNECTED -> "DISCONNECT"
        Phase.PREPARING, Phase.CONNECTING -> "CANCEL"
        else -> "CONNECT"
    }
    val busy = state.phase == Phase.PREPARING || state.phase == Phase.CONNECTING

    val animatedAccent by animateColorAsState(accent, tween(600), label = "accent")

    // A soft light pulse runs only while a connection attempt is in progress.
    // Idle and connected states stay static; only the accent color animates.
    val motion = rememberInfiniteTransition(label = "connect-motion")
    val pulse = motion.animateFloat(
        initialValue = 0f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(
            animation = tween(2000, easing = FastOutSlowInEasing),
            repeatMode = RepeatMode.Restart,
        ),
        label = "pulse",
    )
    val delayedPulse = remember { derivedStateOf { (pulse.value + 0.5f) % 1f } }

    Box(
        modifier = Modifier.size(236.dp),
        contentAlignment = Alignment.Center,
    ) {
        Box(
            modifier = Modifier
                .size(236.dp)
                .clip(CircleShape)
                .background(animatedAccent.copy(alpha = 0.07f)),
        )
        if (busy) {
            LightPulse(color = animatedAccent.copy(alpha = 0.85f), phase = pulse)
            LightPulse(color = animatedAccent.copy(alpha = 0.65f), phase = delayedPulse)
        }

        Box(
            modifier = Modifier
                .size(198.dp)
                .clip(CircleShape)
                .background(animatedAccent.copy(alpha = 0.16f)),
        )
        Box(
            modifier = Modifier
                .size(166.dp)
                .clip(CircleShape)
                .background(
                    Brush.radialGradient(
                        listOf(animatedAccent, animatedAccent.copy(alpha = 0.82f)),
                    ),
                )
                .clickable(onClick = onClick),
            contentAlignment = Alignment.Center,
        ) {
            Column(
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.Center,
            ) {
                Text(
                    text = label,
                    fontSize = 17.sp,
                    fontWeight = FontWeight.Bold,
                    color = Color(0xFF04121F),
                    letterSpacing = 1.5.sp,
                )
                if (busy) {
                    Spacer(Modifier.height(8.dp))
                    CircularProgressIndicator(
                        color = Color(0xFF04121F),
                        strokeWidth = 2.dp,
                        modifier = Modifier.size(18.dp),
                    )
                }
            }
        }
    }
}
