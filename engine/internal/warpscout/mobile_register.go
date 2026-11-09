package warpscout

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"time"

	"github.com/vernette/warpscout/core"
)

// TunnelRegistration is a first-run registration performed entirely inside a
// WARP tunnel built from the embedded bootstrap keys. No direct API call, no
// relay: if no bootstrap tunnel opens, registration does not happen.
type TunnelRegistration struct {
	AccountJSON string `json:"accountJson"`
	Endpoint    string `json:"endpoint"`
	I1          string `json:"i1,omitempty"`
	I1Label     string `json:"i1Label,omitempty"`
	Jc          int    `json:"jc"`
	Jmin        int    `json:"jmin"`
	Jmax        int    `json:"jmax"`
	MTU         int    `json:"mtu"`
}

// RegisterThroughTunnel registers a WARP account using every path warpscout
// offers, in upstream's order: direct API, relay fallback, then a tunnel
// built from the embedded bootstrap keys. Each stage is attempted only if
// the previous one failed.
func (b *MobileBackend) RegisterThroughTunnel(ctx context.Context, scan core.ScanOptions, sink core.EventSink) (TunnelRegistration, error) {
	b.mu.Lock()
	defer b.mu.Unlock()

	resetMobileState()
	timeoutSec := positiveOr(scan.TimeoutSec, 2)

	// 1) Direct Cloudflare registration. Works anywhere with plain outbound
	// HTTPS; stops here when it succeeds.
	emitProgress(sink, core.OperationRegister, "registration", 0, 1, "Trying direct registration")
	direct := &http.Client{Timeout: registerTimeout, Transport: regTransport(nil)}
	if apiReachable(direct) {
		if a, err := mintAccount(ctx, direct, account{}); err == nil {
			emitProgress(sink, core.OperationRegister, "registration", 1, 1, "Registered via the direct API")
			return registrationFrom(a, TunnelRegistration{})
		}
	}

	// 2) Relay fallback — the app's own relay first, then warpscout's
	// upstream relay. Only used when direct registration could not complete.
	emitProgress(sink, core.OperationRegister, "registration", 0, 1, "Direct registration failed; trying relays")
	for _, relay := range []string{customRelay, upstreamRelay} {
		c, err := relayClient(relay)
		if err != nil {
			continue
		}
		emitProgress(sink, core.OperationRegister, "registration", 0, 1,
				fmt.Sprintf("Direct registration failed; trying relay %s", relay))
		if a, err := mintAccount(ctx, c, account{}); err == nil {
			emitProgress(sink, core.OperationRegister, "registration", 1, 1,
				fmt.Sprintf("Registered via the relay %s", relay))
			return registrationFrom(a, TunnelRegistration{})
		}
	}

	// 3) Bootstrap tunnel — the last resort, used when neither direct nor
	// the relays got through. Plain WireGuard and AmneziaWG both get a turn.
	emitProgress(sink, core.OperationRegister, "registration", 0, 1, "Relays failed; trying the bootstrap tunnel")
	opts := options{
		tunnelParallel: defaultTunnelJobs,
		timeoutSec:     timeoutSec,
		perSubnet:      registerSample,
		proto:          protoAWG,
		plain:          true,
	}
	if err := applyMobileTarget(&opts, scan.CustomTarget); err != nil {
		return TunnelRegistration{}, mobileFailure("invalid_target", err, false)
	}
	_, ips, err := setupScan(opts)
	if err != nil {
		return TunnelRegistration{}, mobileFailure("network_unavailable", err, true)
	}
	if len(ips) == 0 {
		return TunnelRegistration{}, mobileFailure("no_endpoints", errors.New("no WARP endpoint pools available"), true)
	}
	sampled := sampleAddrs(ips, tunnelDiscoverySample)
	timeout := time.Duration(opts.timeoutSec) * time.Second

	origI1, origLabel := awgI1, genI1Label
	var lastErr error
	for _, profile := range []protoRun{{kindAWG, protoAWG}, {kindWG, protoWG}} {
		run := profile
		for _, candidate := range regI1Candidates(run, opts, origI1, origLabel) {
			if ctx.Err() != nil {
				break
			}
			// newTunnel bakes the globals into the UAPI config, so set them first.
			awgI1, genI1Label = candidate.chain, candidate.label
			label := profile.name
			if profile.isAWG() && !opts.i1Explicit {
				label += fmt.Sprintf(" (%s)", i1NoteFor(candidate.chain, candidate.label))
			}
			emitProgress(sink, core.OperationRegister, "tunnel", 0, len(sampled),
				fmt.Sprintf("Registering through a WARP tunnel (%s)", label))
			onProbe := func(probed int) {
				emitProgress(sink, core.OperationRegister, "tunnel", probed, len(sampled),
					fmt.Sprintf("Registering through a WARP tunnel (%s)", label))
			}
			acct, endpoint, regErr := registerViaTunnelEndpoint(ctx, run, sampled, timeout, onProbe, account{})
			if regErr != nil {
				lastErr = regErr
				continue
			}
			awgI1, genI1Label = origI1, origLabel
			emitProgress(sink, core.OperationRegister, "registration", 1, 1, "Registered through a WARP tunnel")
			return registrationFrom(acct, TunnelRegistration{
				Endpoint: endpoint,
				I1:       candidate.chain,
				I1Label:  candidate.label,
				Jc:       awgJc,
				Jmin:     awgJmin,
				Jmax:     awgJmax,
				MTU:      tunnelMTU,
			})
		}
	}
	awgI1, genI1Label = origI1, origLabel
	if lastErr == nil {
		lastErr = context.Canceled
	}
	return TunnelRegistration{}, operationFailure(ctx, "registration_failed", lastErr, true)
}

func registrationFrom(a account, reg TunnelRegistration) (TunnelRegistration, error) {
	raw, err := json.Marshal(a)
	if err != nil {
		return TunnelRegistration{}, mobileFailure("account_encoding_failed", err, false)
	}
	reg.AccountJSON = string(raw)
	if reg.MTU == 0 {
		reg.MTU = tunnelMTU
	}
	return reg, nil
}
