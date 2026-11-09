package mobileapi

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"sync"

	"github.com/vernette/warpscout/core"
	"github.com/vernette/warpscout/internal/warpscout"
)

var (
	regBackend  = warpscout.NewMobileBackend()
	auxMu       sync.Mutex
	auxCancel   context.CancelFunc
	auxActiveIn bool
)

// TunnelUp hands an Android VpnService TUN fd to amneziawg-go. specJSON is a
// warpscout.TunnelSpec. It returns the live statistics JSON.
func TunnelUp(specJSON string, tunFD int) (result string, err error) {
	defer func() {
		if recovered := recover(); recovered != nil {
			err = fmt.Errorf("tunnel panic: %v", recovered)
		}
	}()
	return warpscout.TunnelUp(specJSON, tunFD)
}

// TunnelDown stops the live tunnel and closes the TUN fd.
func TunnelDown() (err error) {
	defer func() {
		if recovered := recover(); recovered != nil {
			err = fmt.Errorf("tunnel panic: %v", recovered)
		}
	}()
	return warpscout.TunnelDown()
}

// TunnelStats returns handshake and transfer counters for the live tunnel.
func TunnelStats() (result string, err error) {
	defer func() {
		if recovered := recover(); recovered != nil {
			err = fmt.Errorf("tunnel panic: %v", recovered)
		}
	}()
	return warpscout.TunnelStats()
}

// TunnelSocketFDs returns the UDP socket descriptors the tunnel is using, so
// VpnService.protect() can be applied to them.
func TunnelSocketFDs() (result string, err error) {
	defer func() {
		if recovered := recover(); recovered != nil {
			err = fmt.Errorf("tunnel panic: %v", recovered)
		}
	}()
	return warpscout.TunnelSocketFDs()
}

// RegisterTunnel registers a fresh WARP account through a tunnel built from the
// embedded bootstrap keys. It never calls the API directly and never uses a
// relay. optionsJSON is a core.ScanOptions.
func RegisterTunnel(optionsJSON string, listener Listener) (err error) {
	defer func() {
		if recovered := recover(); recovered != nil {
			err = fmt.Errorf("core panic: %v", recovered)
			emitError(listener, "core_panic", "the core stopped unexpectedly", false)
		}
	}()

	var options core.ScanOptions
	if err := json.Unmarshal([]byte(optionsJSON), &options); err != nil {
		emitError(listener, "invalid_payload", "scan options are invalid", false)
		return err
	}

	ctx, cancel := context.WithCancel(context.Background())
	auxMu.Lock()
	if auxActiveIn {
		auxMu.Unlock()
		cancel()
		return errors.New("another operation is already active")
	}
	auxActiveIn = true
	auxCancel = cancel
	auxMu.Unlock()
	defer func() {
		cancel()
		auxMu.Lock()
		auxActiveIn = false
		auxCancel = nil
		auxMu.Unlock()
	}()

	sink := func(event core.ProgressEvent) {
		event.SchemaVersion = 1
		emit(listener, event)
	}
	result, err := regBackend.RegisterThroughTunnel(ctx, options, sink)
	if err != nil {
		emitCoreError(listener, err)
		return err
	}
	emit(listener, responseEvent{
		SchemaVersion: 1,
		Type:          "completed",
		Operation:     string(core.OperationRegister),
		Payload:       result,
	})
	return nil
}

// cancelAux stops an in-flight RegisterTunnel call, if any.
func cancelAux() {
	auxMu.Lock()
	cancel := auxCancel
	auxMu.Unlock()
	if cancel != nil {
		cancel()
	}
}
