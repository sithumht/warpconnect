//go:build !linux && !windows

// The mobile_tun_other.go shim used to hard-error on non-Linux hosts. We no
// longer need it now that Windows has its own implementation; any host that is
// neither Linux-family nor Windows gets a blunt "not supported" error, just
// like before.
package warpscout

import (
	"errors"

	"github.com/amnezia-vpn/amneziawg-go/tun"
)

func openTUNDevice(int) (tun.Device, string, error) {
	return nil, "", errors.New("attaching a desktop TUN device is only supported on Windows (see mobile_tun_windows.go)")
}
