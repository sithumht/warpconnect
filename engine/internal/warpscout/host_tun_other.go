//go:build !windows

package warpscout

import (
	"errors"

	"github.com/amnezia-vpn/amneziawg-go/tun"
)

// openHostTUN and teardownHostTUN exist so the desktop bring-up path compiles on
// every platform; on anything but Windows they are clear errors instead of silent
// no-ops.
func openHostTUN(ipv4, endpoint string) (tun.Device, string, error) {
	return nil, "", errors.New("desktop tunnel bring-up is only implemented on Windows")
}

func teardownHostTUN() error {
	return nil
}
