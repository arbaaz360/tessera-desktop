import { useEffect, useRef, useState, type ReactNode } from 'react';

/**
 * A confirmation dialog for actions with consequences.
 *
 * Two tiers, matched to what is at stake:
 *
 *   - Plain confirm: for reversible actions (move to trash, big download).
 *     One click through, Enter accepts, Escape cancels.
 *   - Typed confirm (`requireText`): for the irreversible one — emptying the
 *     trash. Requiring the user to type a word converts a reflex click into a
 *     deliberate act; it is the difference between a speed bump and a gate.
 *
 * Deliberately NOT a portal. It renders where it is used, which keeps it
 * stacked correctly above whatever opened it (including the full-screen
 * viewer at z-50) without a z-index arms race — the overlay is
 * `fixed inset-0`, so DOM position does not affect coverage, only paint
 * order, and being later in the DOM is exactly the order we want.
 *
 * That choice has one sharp edge, which is why `pointer-events-auto` is on the
 * overlay below and must stay there. Every floating bar in this app — the
 * selection bar, the trash bar, the merge bar — wraps itself in
 * `pointer-events-none` so the page behind it stays usable, and re-enables
 * pointer events only on the bar itself. A dialog rendered inside one of those
 * wrappers INHERITS `pointer-events: none`: it paints perfectly and ignores
 * every click. That shipped once, and the merge dialog looked hung when in
 * fact no click was reaching it. Setting it here fixes all call sites at once.
 */
export interface ConfirmDialogProps {
  title: string;
  /** Body copy. Say what happens, including how to undo it — or that you cannot. */
  children: ReactNode;
  confirmLabel: string;
  /** Styles the confirm button for destructive actions. */
  danger?: boolean;
  /** When set, the confirm button stays disabled until this word is typed. */
  requireText?: string;
  /** Shows a spinner-ish disabled state while the mutation runs. */
  busy?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

export function ConfirmDialog({
  title,
  children,
  confirmLabel,
  danger,
  requireText,
  busy,
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  const [typed, setTyped] = useState('');
  const confirmRef = useRef<HTMLButtonElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const armed = !requireText || typed.trim().toLowerCase() === requireText.toLowerCase();

  // Focus lands on the thing the user must act on: the input when typing is
  // required, otherwise the confirm button so Enter completes the flow.
  useEffect(() => {
    (requireText ? inputRef.current : confirmRef.current)?.focus();
  }, [requireText]);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        // Owners of outer Escape handlers (the viewer) gate on their own
        // dialog state; stopping propagation here is belt and braces.
        event.stopPropagation();
        onCancel();
      }
    };
    window.addEventListener('keydown', onKey, { capture: true });
    return () => window.removeEventListener('keydown', onKey, { capture: true });
  }, [onCancel]);

  return (
    <div
      className="pointer-events-auto fixed inset-0 z-[60] flex items-center justify-center bg-black/60 p-4 backdrop-blur-sm"
      role="dialog"
      aria-modal="true"
      aria-label={title}
      onMouseDown={(event) => {
        // Backdrop click cancels; clicks inside the panel do not bubble here
        // because the panel stops them below.
        if (event.target === event.currentTarget) onCancel();
      }}
    >
      <div
        className="w-full max-w-sm rounded-lg border border-line bg-overlay p-5 shadow-xl"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <h2 className="text-base font-semibold text-ink">{title}</h2>
        <div className="mt-2 text-sm leading-relaxed text-ink-muted">{children}</div>

        {requireText && (
          <input
            ref={inputRef}
            type="text"
            value={typed}
            onChange={(event) => setTyped(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter' && armed && !busy) onConfirm();
            }}
            placeholder={`Type "${requireText}" to confirm`}
            className="mt-3 w-full rounded-md border border-line bg-surface px-3 py-2 text-sm text-ink placeholder:text-ink-faint focus:outline-none focus-visible:border-accent"
            aria-label={`Type ${requireText} to confirm`}
          />
        )}

        <div className="mt-4 flex justify-end gap-2">
          <button
            type="button"
            onClick={onCancel}
            disabled={busy}
            className="rounded-md border border-line px-3 py-1.5 text-sm font-medium text-ink-muted transition-colors hover:text-ink disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            ref={confirmRef}
            type="button"
            onClick={onConfirm}
            disabled={!armed || busy}
            className={`rounded-md px-3 py-1.5 text-sm font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${
              danger
                ? 'bg-danger text-white hover:opacity-90'
                : 'bg-accent text-ink-inverse hover:bg-accent-hover'
            }`}
          >
            {busy ? 'Working…' : confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
