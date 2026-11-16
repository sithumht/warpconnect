namespace WarpConnect {
    // Seeds verified on ATOM/U9 (Myanmar) — same list as Android Seeds.kt.
    // Each entry still goes through the same handshake+verify gate, so trying
    // them is a pure win when they work and costs one failed attempt when
    // they don't.
    public static class Seeds {
        public static readonly string[] Endpoints = new[]
        {
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
        };
    }
}
