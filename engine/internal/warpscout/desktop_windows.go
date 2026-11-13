//go:build windows

package warpscout

import (
	"fmt"
	"os/exec"
	"strings"

	"github.com/amnezia-vpn/amneziawg-go/tun"
	"golang.org/x/sys/windows"
)

// Desktop bring-up uses a WinTUN adapter with a fixed name and GUID: wintun
// re-attaches to an existing adapter when the GUID matches, so re-running the
// tunnel command never leaves a pile of dead virtual adapters behind.
const (
	desktopAdapterName = "Warpgate"
	desktopAdapterGUID = "{d20cbe7b-4c5a-4e1e-9b8f-7a3e5f2a9c01}"
)

// desktopAdapter is the name the OS actually gave the adapter (Windows renames
// it when a stale copy exists). Populated by createWinTUNDevice.
var desktopAdapter string

// createWinTUNDevice builds the desktop WARP interface:
//
//   - adapter "Warpgate" with the account's IPv4 assigned /32
//   - 0.0.0.0/1 and 128.0.0.0/1 routed on-link through the adapter (the same
//     split WireGuard uses; the host default route keeps working)
//   - a /32 host route for the endpoint itself through the physical default
//     gateway — without it the tunnel's own UDP packets would be routed into
//     the tunnel (longest-prefix match wins over the /1 routes)
//   - 1.1.1.1 as the adapter's DNS
//
// Call teardownWinTUNDevice when done. Requires elevation.
func createWinTUNDevice(ipv4, endpoint string) (tun.Device, string, error) {
	guid, err := windows.GUIDFromString(desktopAdapterGUID)
	if err != nil {
		return nil, "", fmt.Errorf("adapter GUID: %w", err)
	}
	dev, err := tun.CreateTUNWithRequestedGUID(desktopAdapterName, &guid, 1280)
	if err != nil {
		return nil, "", fmt.Errorf("create WinTUN adapter (run elevated): %w", err)
	}
	ifname, err := dev.Name()
	if err != nil {
		_ = dev.Close()
		return nil, "", fmt.Errorf("adapter name: %w", err)
	}
	desktopAdapter = ifname
	lastEndpoint = endpoint
	cleanup := func() {
		_ = teardownWinTUNDevice(ifname, endpoint)
		_ = dev.Close()
		desktopAdapter = ""
		lastEndpoint = ""
	}

	steps := []struct {
		args []string
	}{
		{[]string{"interface", "ip", "set", "address", "name=" + ifname, "static", ipv4, "255.255.255.255"}},
		{[]string{"interface", "ip", "set", "dns", "name=" + ifname, "static", "1.1.1.1"}},
	}
	for _, step := range steps {
		if err := runArgs("netsh", step.args); err != nil {
			cleanup()
			return nil, "", fmt.Errorf("netsh %s: %w", strings.Join(step.args, " "), err)
		}
	}

	// The endpoint must stay reachable through the physical default gateway or the
	// tunnel would route its own handshake packets into itself.
	gateway, err := defaultGateway()
	if err != nil {
		cleanup()
		return nil, "", fmt.Errorf("default gateway: %w", err)
	}
	if net, split, ok := strings.Cut(endpoint, ":"); ok && split != "" && net != "" {
		// IPv4 endpoints only; a hostname resolves inside the tunnel anyway.
		if err := runArgs("route", []string{"add", net, "mask", "255.255.255.255", gateway, "metric", "1"}); err != nil {
			cleanup()
			return nil, "", fmt.Errorf("endpoint host route: %w", err)
		}
	}
	ifname_idx, err := interfaceIndex(ifname)
	if err != nil {
		cleanup()
		return nil, "", fmt.Errorf("adapter index: %w", err)
	}
	for _, dst := range []string{"128.0.0.0", "0.0.0.0"} {
		if err := runArgs("route", []string{"add", dst, "mask", "128.0.0.0", "0.0.0.0", "metric", "1", "IF", ifname_idx}); err != nil {
			cleanup()
			return nil, "", fmt.Errorf("route add %s (on-link): %w", dst, err)
		}
	}
	return dev, ifname, nil
}

// teardownWinTUNDevice reverses createWinTUNDevice step-by-step. It is safe to
// call on a half-set-up interface: every command is attempted and the problems
// are joined instead of stopping at the first failure.
func teardownWinTUNDevice(ifname, endpoint string) error {
	var problems []string
	collect := func(label string, err error) {
		if err != nil {
			problems = append(problems, label+": "+err.Error())
		}
	}
	for _, dst := range []string{"128.0.0.0", "0.0.0.0"} {
		collect("route delete "+dst, runArgs("route", []string{"delete", dst, "mask", "128.0.0.0", "0.0.0.0"}))
	}
	if host, _, ok := strings.Cut(endpoint, ":"); ok && host != "" {
		collect("route delete endpoint", runArgs("route", []string{"delete", host, "mask", "255.255.255.255"}))
	}
	collect("netsh delete address", runArgs("netsh", []string{"interface", "ip", "delete", "address", "name=" + ifname}))
	if len(problems) > 0 {
		return fmt.Errorf("teardown: %s", strings.Join(problems, "; "))
	}
	return nil
}

// teardownHostTUN removes the routes and the interface after the tunnel closed.
func teardownHostTUN() error {
	ifname := desktopAdapter
	endpoint := lastEndpoint
	desktopAdapter = ""
	lastEndpoint = ""
	if ifname == "" {
		return nil
	}
	return teardownWinTUNDevice(ifname, endpoint)
}

var lastEndpoint string

func defaultGateway() (string, error) {
	out, err := runPowerShell(`(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' | Sort-Object {$_.RouteMetric + $_.InterfaceMetric} | Select-Object -First 1).NextHop`)
	if err != nil {
		return "", err
	}
	if value := strings.TrimSpace(out); value != "" {
		return value, nil
	}
	return "", fmt.Errorf("no default route found")
}

func interfaceIndex(name string) (string, error) {
	out, err := runPowerShell(`(Get-NetAdapter -Name '` + strings.ReplaceAll(name, "'", "''") + `').ifIndex`)
	if err != nil {
		return "", err
	}
	if value := strings.TrimSpace(out); value != "" {
		return value, nil
	}
	return "", fmt.Errorf("adapter %q not found", name)
}

func runPowerShell(script string) (string, error) {
	cmd := exec.Command("powershell", "-NoProfile", "-Command", script)
	out, err := cmd.CombinedOutput()
	if err != nil {
		return "", fmt.Errorf("powershell: %w — %s", err, strings.TrimSpace(string(out)))
	}
	return string(out), nil
}

func runArgs(program string, args []string) error {
	cmd := exec.Command(program, args...)
	out, err := cmd.CombinedOutput()
	if err != nil {
		return fmt.Errorf("%w — %s", err, strings.TrimSpace(string(out)))
	}
	return nil
}

// openHostTUN creates (or re-attaches to) the desktop WARP adapter described by
// createWinTUNDevice. The IPv4 address is the account's assigned interface
// address — the peer drops inner packets sourced from anywhere else.
func openHostTUN(ipv4, endpoint string) (tun.Device, string, error) {
	return createWinTUNDevice(ipv4, endpoint)
}
