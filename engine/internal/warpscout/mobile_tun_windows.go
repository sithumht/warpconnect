//go:build windows

package warpscout

import (
	"errors"

	"github.com/amnezia-vpn/amneziawg-go/tun"
)

// openTUNDevice is the Linux VpnService fd path and has no meaning on Windows.
// The desktop tunnel creates its own WinTUN adapter through openHostTUN instead.
func openTUNDevice(_ int) (tun.Device, string, error) {
	return nil, "", errors.New("the mobile VpnService tun fd path is not supported on Windows")
}
