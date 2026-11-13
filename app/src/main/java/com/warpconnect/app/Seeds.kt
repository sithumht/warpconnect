package com.warpconnect.app

/**
 * Endpoints known to work on strict networks (verified on ATOM/U9, Myanmar).
 * Used only as a fast-first-try list on a cold install with no cached
 * endpoint — each entry goes through the same handshake+data verification as
 * any scanned candidate, so trying these first is a pure win when they work
 * and harmless when they don't (scan still runs after).
 *
 * These are public Cloudflare WARP endpoints — bundling IPs is safe and
 * ships no credentials.
 */
object Seeds {
    /** Ordered by empirical success rate on the test network. */
    val ENDPOINTS = listOf(
        "188.114.99.137:500",
        "188.114.99.143:500",
        "188.114.99.149:500",
        "188.114.99.155:500",
        "188.114.99.162:500",
        "188.114.99.170:500",
        "188.114.99.174:500",
        "162.159.192.1:500",
        "162.159.193.5:500",
        "162.159.193.16:500",
        "162.159.195.1:500",
        "8.6.112.178:500",
    )
}
