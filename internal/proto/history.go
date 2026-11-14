package proto

// TypeFluxClipboardHistory lets a phone read and search its own entries
// of the clipboard history of this computer. The phone asks, fluxd
// answers. docs/features.md describes it.
const TypeFluxClipboardHistory = "flux.clipboard.history"

func init() {
	Incoming = append(Incoming, TypeFluxClipboardHistory)
	Outgoing = append(Outgoing, TypeFluxClipboardHistory)
}
