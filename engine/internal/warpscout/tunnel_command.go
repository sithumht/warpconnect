package warpscout

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/netip"
	"os"
	"os/signal"
	"strings"
	"time"
)

// runTunnelCmd brings up a full IPv4 WARP tunnel on the host. Windows uses a
// WinTUN adapter; on Linux/Android there is no VpnService to lean on, so the
// command reports that instead of pretending.
func runTunnelCmd(ctx context.Context, opts options) error {
	if opts.endpoint == "" {
		return errors.New("-endpoint ADDR is required: pass what \"warpscout scan -best\" prints (IPv4 ip:port)")
	}
	ap, err := netip.ParseAddrPort(opts.endpoint)
	if err != nil {
		return fmt.Errorf("invalid -endpoint %q: want IPv4 ip:port", opts.endpoint)
	}
	if !ap.Addr().Is4() {
		return fmt.Errorf("-endpoint must be an IPv4 literal (got %s) â€” the desktop tunnel routes IPv4", ap.Addr())
	}
	endpoint := ap.String()

	// Register for interrupts before anything is created â€” a hard kill midway
	// through bring-up would otherwise strand routes pointing at a dead adapter.
	ctx, stop := signal.NotifyContext(ctx, os.Interrupt)
	defer stop()

	a, err := loadAccount(opts.accountPath)
	if err != nil {
		return err
	}
	interfaceIPv4 := strings.SplitN(a.IPv4, "/", 2)[0]
	if interfaceIPv4 == "" {
		return fmt.Errorf("%s holds no IPv4 address", opts.accountPath)
	}

	// The account carries no AmneziaWG profile, so the profile comes from the
	// same flags the scan commands accept: -proto awg with -jc/-jmin/-jmax/-i1,
	// or -proto wg for a plain WireGuard tunnel.
	spec := TunnelSpec{
		PrivateKey:    a.PrivateKey,
		PeerPublicKey: a.PeerPublicKey,
		Endpoint:      endpoint,
		Keepalive:     keepalive,
	}
	switch opts.proto {
	case protoAWG:
		spec.Jc, spec.Jmin, spec.Jmax, spec.I1 = awgJc, awgJmin, awgJmax, awgI1
	case protoWG:
	default:
		return fmt.Errorf("-proto must be wg or awg, not %q", opts.proto)
	}
	specJSON, err := json.Marshal(spec)
	if err != nil {
		return err
	}

	fmt.Fprintln(os.Stderr, errPal.dim(fmt.Sprintf("Creating the Warpgate adapter and dialling %s (AmneziaWG)â€¦", endpoint)))
	if _, err := TunnelUpDesktop(string(specJSON), interfaceIPv4); err != nil {
		return err
	}
	defer func() {
		_ = TunnelDown()
	}()

	// The handshake is the only honest "it works" signal on a DPI'd network â€”
	// the route being in place proves nothing. Ctrl+C during the wait still
	// tears the adapter down instead of leaving a blackholed route table.
	deadline := time.Now().Add(handshakeWait)
	for {
		select {
		case <-ctx.Done():
			fmt.Fprintln(os.Stderr, errPal.dim("Interrupted â€” tearing downâ€¦"))
			return nil
		default:
		}
		st, err := readTunnelStats()
		if err == nil && st.HandshakeTime > 0 {
			fmt.Fprintln(os.Stderr, errPal.ok(fmt.Sprintf("WARP tunnel is up (handshake %ds ago) â€” IPv4 traffic now exits through %s", st.HandshakeAgeMS/1000, endpoint)))
			break
		}
		if time.Now().After(deadline) {
			fmt.Fprintln(os.Stderr, errPal.fail(fmt.Sprintf("no handshake within %s â€” routes are up but the endpoint is not answering; Ctrl+C to tear down", handshakeWait)))
			break
		}
		time.Sleep(pollInterval)
	}

	fmt.Fprintln(os.Stderr, errPal.dim("DNS is 1.1.1.1 on the adapter; the endpoint's own UDP rides the physical default route."))
	fmt.Fprintln(os.Stderr, errPal.dim("Ctrl+C disconnects and removes the adapter and routes."))

	ticker := time.NewTicker(statsInterval)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			fmt.Fprintln(os.Stderr, errPal.dim("Disconnectingâ€¦"))
			return nil
		case <-ticker.C:
			if st, err := readTunnelStats(); err == nil && st.Up {
				fmt.Fprintf(os.Stderr, "%s rx %s Â· tx %s\n",
					errPal.dim("tunnel"),
					formatBytes(st.RxBytes),
					formatBytes(st.TxBytes),
				)
			}
		}
	}
}

const (
	handshakeWait  = 20 * time.Second
	pollInterval   = 500 * time.Millisecond
	statsInterval  = 30 * time.Second
)

func readTunnelStats() (tunnelStats, error) {
	var st tunnelStats
	raw, err := TunnelStats()
	if err != nil {
		return st, err
	}
	if err := json.Unmarshal([]byte(raw), &st); err != nil {
		return st, err
	}
	return st, nil
}

func formatBytes(n int64) string {
	const unit = 1024
	if n < unit {
		return fmt.Sprintf("%d B", n)
	}
	div, exp := int64(unit), 0
	for v := n / unit; v >= unit; v /= unit {
		div *= unit
		exp++
	}
	return fmt.Sprintf("%.1f %ciB", float64(n)/float64(div), "KMGTPE"[exp])
}

