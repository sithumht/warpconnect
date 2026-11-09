package warpscout

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/amnezia-vpn/amneziawg-go/conn"
	"github.com/amnezia-vpn/amneziawg-go/device"
)

// TunnelSpec is everything the Android VpnService needs to hand a live TUN file
// descriptor to amneziawg-go. It is deliberately transport-only: addresses and
// routes are already installed by VpnService.Builder, so the device only has to
// encrypt what the kernel sends into the fd.
type TunnelSpec struct {
	PrivateKey    string `json:"privateKey"`
	PeerPublicKey string `json:"peerPublicKey"`
	Endpoint      string `json:"endpoint"`
	I1            string `json:"i1,omitempty"`
	Jc            int    `json:"jc,omitempty"`
	Jmin          int    `json:"jmin,omitempty"`
	Jmax          int    `json:"jmax,omitempty"`
	Keepalive     int    `json:"keepalive,omitempty"`
}

type tunnelStats struct {
	Up             bool   `json:"up"`
	HandshakeTime  int64  `json:"handshakeTime"`
	HandshakeAgeMS int64  `json:"handshakeAgeMs"`
	RxBytes        int64  `json:"rxBytes"`
	TxBytes        int64  `json:"txBytes"`
	Endpoint       string `json:"endpoint,omitempty"`
}

var (
	liveTunnelMu sync.Mutex
	liveTunnel   *device.Device
)

func tunnelUAPI(s TunnelSpec) (string, error) {
	if strings.TrimSpace(s.PrivateKey) == "" {
		return "", errors.New("missing private key")
	}
	if strings.TrimSpace(s.PeerPublicKey) == "" {
		return "", errors.New("missing peer public key")
	}
	if strings.TrimSpace(s.Endpoint) == "" {
		return "", errors.New("missing endpoint")
	}
	privHex, err := base64ToHex(s.PrivateKey)
	if err != nil {
		return "", fmt.Errorf("private key: %w", err)
	}
	pubHex, err := base64ToHex(s.PeerPublicKey)
	if err != nil {
		return "", fmt.Errorf("peer public key: %w", err)
	}

	var b strings.Builder
	fmt.Fprintf(&b, "private_key=%s\n", privHex)
	if s.Jc > 0 {
		fmt.Fprintf(&b, "jc=%d\n", s.Jc)
	}
	if s.Jmin > 0 {
		fmt.Fprintf(&b, "jmin=%d\n", s.Jmin)
	}
	if s.Jmax > 0 {
		fmt.Fprintf(&b, "jmax=%d\n", s.Jmax)
	}
	if s.I1 != "" {
		fmt.Fprintf(&b, "i1=%s\n", s.I1)
	}
	fmt.Fprintf(&b, "replace_peers=true\n")
	fmt.Fprintf(&b, "public_key=%s\n", pubHex)
	fmt.Fprintf(&b, "endpoint=%s\n", s.Endpoint)
	fmt.Fprintf(&b, "allowed_ip=0.0.0.0/0\n")
	fmt.Fprintf(&b, "allowed_ip=::/0\n")
	keepaliveInterval := s.Keepalive
	if keepaliveInterval <= 0 {
		keepaliveInterval = keepalive
	}
	fmt.Fprintf(&b, "persistent_keepalive_interval=%d\n", keepaliveInterval)
	return b.String(), nil
}

// TunnelUp attaches amneziawg-go to an already-configured Android TUN fd. The fd
// is configured by VpnService.Builder, so no ioctl is performed here -
// CreateUnmonitoredTUNFromFD is the same entry point the official AmneziaWG
// Android backend uses.
//
// Ownership of tunFD transfers to this function on entry: on success it is
// closed by TunnelDown, and on every error path it is closed before returning.
// The caller must therefore never close it again after handing it over, or it
// could close a recycled descriptor number.
func TunnelUp(specJSON string, tunFD int) (string, error) {
	if tunFD <= 0 {
		return "", errors.New("invalid tun fd")
	}
	var spec TunnelSpec
	if err := json.Unmarshal([]byte(specJSON), &spec); err != nil {
		closeTunFD(tunFD)
		return "", fmt.Errorf("invalid tunnel spec: %w", err)
	}
	uapi, err := tunnelUAPI(spec)
	if err != nil {
		closeTunFD(tunFD)
		return "", err
	}

	liveTunnelMu.Lock()
	defer liveTunnelMu.Unlock()
	if liveTunnel != nil {
		liveTunnel.Close()
		liveTunnel = nil
	}
	tunDev, _, err := openTUNDevice(tunFD)
	if err != nil {
		// CreateUnmonitoredTUNFromFD only fails before it takes ownership, so
		// the descriptor is still ours to close here.
		closeTunFD(tunFD)
		return "", fmt.Errorf("attach tun: %w", err)
	}
	dev := device.NewDevice(tunDev, conn.NewStdNetBind(), device.NewLogger(device.LogLevelSilent, ""))
	if err := dev.IpcSet(uapi); err != nil {
		dev.Close()
		return "", fmt.Errorf("apply config: %w", err)
	}
	dev.DisableSomeRoamingForBrokenMobileSemantics()
	if err := dev.Up(); err != nil {
		dev.Close()
		return "", fmt.Errorf("device up: %w", err)
	}
	liveTunnel = dev
	return tunnelStatsJSON(dev)
}

// closeTunFD closes a descriptor that has not been wrapped by a device yet.
func closeTunFD(fd int) {
	if fd <= 0 {
		return
	}
	_ = os.NewFile(uintptr(fd), "tun-fd").Close()
}

// TunnelDown tears the device down and closes the TUN fd with it.
func TunnelDown() error {
	liveTunnelMu.Lock()
	defer liveTunnelMu.Unlock()
	if liveTunnel == nil {
		return nil
	}
	liveTunnel.Close()
	liveTunnel = nil
	return nil
}

// TunnelStats reports whether the peer has actually handshaked since it came up.
func TunnelStats() (string, error) {
	liveTunnelMu.Lock()
	defer liveTunnelMu.Unlock()
	if liveTunnel == nil {
		return `{"up":false}`, nil
	}
	return tunnelStatsJSON(liveTunnel)
}

// TunnelSocketFDs reports the UDP socket descriptors amneziawg-go is actually
// using, so the VpnService can protect() them and keep its own traffic out of
// the tunnel it owns.
func TunnelSocketFDs() (string, error) {
	liveTunnelMu.Lock()
	defer liveTunnelMu.Unlock()
	result := struct {
		V4 int `json:"v4"`
		V6 int `json:"v6"`
	}{V4: -1, V6: -1}
	if liveTunnel == nil {
		data, _ := json.Marshal(result)
		return string(data), nil
	}
	bind, ok := liveTunnel.Bind().(conn.PeekLookAtSocketFd)
	if !ok {
		data, _ := json.Marshal(result)
		return string(data), nil
	}
	result.V4 = peekSocketFD(bind.PeekLookAtSocketFd4)
	result.V6 = peekSocketFD(bind.PeekLookAtSocketFd6)
	data, err := json.Marshal(result)
	if err != nil {
		return "", err
	}
	return string(data), nil
}

// peekSocketFD returns the descriptor behind a PeekLookAtSocketFd callback, or
// -1 when it is unavailable. The IPv6 callback in particular dereferences a nil
// socket whenever the platform refused to open the optional IPv6 UDP socket, so
// a panic there must not take away the (working) IPv4 descriptor.
func peekSocketFD(peek func() (int, error)) (fd int) {
	fd = -1
	defer func() {
		if recover() != nil {
			fd = -1
		}
	}()
	if got, err := peek(); err == nil && got > 0 {
		fd = got
	}
	return fd
}

func tunnelStatsJSON(dev *device.Device) (string, error) {
	conf, err := dev.IpcGet()
	if err != nil {
		return "", fmt.Errorf("read stats: %w", err)
	}
	st := parseTunnelStats(conf)
	st.Up = true
	data, err := json.Marshal(st)
	if err != nil {
		return "", err
	}
	return string(data), nil
}

func parseTunnelStats(conf string) tunnelStats {
	var st tunnelStats
	for _, line := range strings.Split(conf, "\n") {
		key, value, ok := strings.Cut(line, "=")
		if !ok {
			continue
		}
		switch key {
		case "endpoint":
			st.Endpoint = value
		case "rx_bytes":
			st.RxBytes, _ = strconv.ParseInt(value, 10, 64)
		case "tx_bytes":
			st.TxBytes, _ = strconv.ParseInt(value, 10, 64)
		case "last_handshake_time_sec":
			st.HandshakeTime, _ = strconv.ParseInt(value, 10, 64)
		}
	}
	if st.HandshakeTime > 0 {
		st.HandshakeAgeMS = time.Now().UnixMilli() - st.HandshakeTime*1000
	}
	return st
}
