//go:build linux

package warpscout

import "github.com/amnezia-vpn/amneziawg-go/tun"

// openTUNDevice attaches to the fd VpnService already configured. Android
// satisfies the linux build tag, and this is the same call the official
// AmneziaWG Android backend makes.
func openTUNDevice(fd int) (tun.Device, string, error) {
	return tun.CreateUnmonitoredTUNFromFD(fd)
}
