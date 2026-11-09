//go:build !linux

package warpscout

import (
	"errors"

	"github.com/amnezia-vpn/amneziawg-go/tun"
)

// openTUNDevice only has a real implementation on Android/Linux. The desktop
// build exists so the engine can still be compiled and tested on the host.
func openTUNDevice(int) (tun.Device, string, error) {
	return nil, "", errors.New("attaching a VpnService tun fd is only supported on Android")
}
