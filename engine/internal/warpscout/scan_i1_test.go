package warpscout

import (
	"context"
	"encoding/json"
	"errors"
	"reflect"
	"testing"

	"github.com/vernette/warpscout/core"
)

func i1TestResult(working, total int) phaseResult {
	results := make([]endpointResult, total)
	for i := 0; i < working; i++ {
		results[i] = endpointResult{ok: true, durable: true}
	}
	return phaseResult{results: results}
}

func TestScanI1RetriesUntilThreshold(t *testing.T) {
	defer resetMobileState()
	resetMobileState()
	ports := append([]int(nil), warpPorts...)
	var tried []string
	var attempts []core.ProgressEvent
	counts := []int{0, 20, 21}
	ph, err := scanWithI1(context.Background(), true, func(e core.ProgressEvent) {
		attempts = append(attempts, e)
	}, func() (phaseResult, error) {
		if !reflect.DeepEqual(warpPorts, ports) {
			t.Fatal("port discovery state leaked between I1 attempts")
		}
		warpPorts = []int{4500}
		tried = append(tried, awgI1)
		return i1TestResult(counts[len(tried)-1], 42), nil
	})
	if err != nil {
		t.Fatal(err)
	}
	want := []string{findJunkI1Third, findJunkI1Second, findJunkI1Fourth}
	if !reflect.DeepEqual(tried, want) || scoreJunk(ph).working != 21 || awgI1 != want[2] {
		t.Fatal("unexpected I1 selection or result")
	}
	for i, event := range attempts {
		if event.Phase != "awg-i1" || event.Completed != i+1 || event.Total != 5 {
			t.Fatalf("unexpected attempt event: %+v", event)
		}
	}
}

func TestScanI1ExhaustsCandidates(t *testing.T) {
	defer resetMobileState()
	calls := 0
	_, err := scanWithI1(context.Background(), true, nil, func() (phaseResult, error) {
		calls++
		if calls == 1 {
			return phaseResult{}, errors.New("no reachable port")
		}
		return i1TestResult(0, 42), nil
	})
	if err == nil || calls != 5 || awgI1 != i1Legacy {
		t.Fatalf("calls=%d, error=%v", calls, err)
	}
}

func TestScanI1FallsBackToBestCompletedAttempt(t *testing.T) {
	defer resetMobileState()
	resetMobileState()
	calls := 0
	var selected core.ProgressEvent
	ph, err := scanWithI1(context.Background(), true, func(e core.ProgressEvent) {
		if e.Phase == "awg-i1-selected" {
			selected = e
		}
	}, func() (phaseResult, error) {
		calls++
		warpPorts = []int{1000 + calls}
		switch calls {
		case 1:
			return i1TestResult(10, 42), nil
		case 2, 3:
			return i1TestResult(20, 42), nil
		case 4:
			return i1TestResult(1, 42), nil
		default:
			return phaseResult{}, errors.New("no reachable port")
		}
	})
	if err != nil || calls != 5 || awgI1 != findJunkI1Second || scoreJunk(ph).working != 20 {
		t.Fatalf("calls=%d working=%d error=%v", calls, scoreJunk(ph).working, err)
	}
	if !reflect.DeepEqual(warpPorts, []int{1002}) {
		t.Fatalf("wrong selected ports: %v", warpPorts)
	}
	var payload map[string]int
	if err := json.Unmarshal(selected.Payload, &payload); err != nil {
		t.Fatal(err)
	}
	if selected.Completed != 42 || selected.Total != 42 || payload["working"] != 20 || payload["attempt"] != 2 {
		t.Fatalf("wrong selected progress: %+v, %v", selected, payload)
	}
}

func TestScanI1ManualAndCancellation(t *testing.T) {
	defer resetMobileState()
	awgI1 = "<r 4>"
	calls := 0
	_, err := scanWithI1(context.Background(), false, nil, func() (phaseResult, error) {
		calls++
		if awgI1 != "<r 4>" {
			t.Fatal("manual I1 replaced")
		}
		return i1TestResult(10, 42), nil
	})
	if err != nil || calls != 1 {
		t.Fatalf("calls=%d, error=%v", calls, err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	calls = 0
	_, err = scanWithI1(ctx, true, nil, func() (phaseResult, error) {
		calls++
		cancel()
		return i1TestResult(42, 42), nil
	})
	if !errors.Is(err, context.Canceled) || calls != 1 {
		t.Fatalf("calls=%d, error=%v", calls, err)
	}
}

func TestScanI1RejectsTornTunnelsAndAcceptsLastProfile(t *testing.T) {
	defer resetMobileState()
	calls := 0
	_, err := scanWithI1(context.Background(), true, nil, func() (phaseResult, error) {
		calls++
		ph := i1TestResult(42, 42)
		if calls < 5 {
			for i := range ph.results {
				ph.results[i].durable = false
			}
		}
		return ph, nil
	})
	if err != nil || calls != 5 || awgI1 != i1Legacy {
		t.Fatalf("calls=%d, error=%v", calls, err)
	}
}
