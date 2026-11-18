package com.warpconnect.app

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.PowerManager
import android.provider.Settings

/**
 * Keeps the tunnel alive on stock Android, whose Doze / App Standby /
 * background limits would otherwise park the service or starve the
 * wake-locks that drive endpoint probing.
 *
 * Two user-controlled moves make the biggest difference:
 *   1. "Use battery without restrictions" — the app is exempt from Doze
 *      and bucket throttling (requested via [batterySettingsIntent]).
 *   2. "Always-on VPN" in Android's VPN settings — Android restarts the
 *      tunnel at boot even before the app is opened ([alwaysOnVpnIntent]).
 *
 * Battery state is re-checked on every screen entry; the hint card itself
 * is dismissible and remembered.
 */
object Persistence {
    private const val PREFS = "persistence"
    private const val KEY_HIDDEN = "hint_hidden"

    fun isUnrestricted(context: Context): Boolean {
        val pm = context.getSystemService(Context.POWER_SERVICE) as? PowerManager ?: return false
        return pm.isIgnoringBatteryOptimizations(context.packageName)
    }

    fun shouldShow(context: Context): Boolean =
        !context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_HIDDEN, false)

    fun hide(context: Context) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .edit().putBoolean(KEY_HIDDEN, true).apply()
    }

    fun batterySettingsIntent(context: Context): Intent =
        Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
            data = Uri.parse("package:${context.packageName}")
        }

    fun alwaysOnVpnIntent(): Intent = Intent(Settings.ACTION_VPN_SETTINGS)
}
