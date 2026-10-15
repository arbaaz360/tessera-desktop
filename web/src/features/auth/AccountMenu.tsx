import { useEffect, useRef, useState } from 'react';
import { useNavigate, useRouter } from '@tanstack/react-router';
import { useQueryClient } from '@tanstack/react-query';
import { getMyUser, logout } from '@immich/sdk';
import type { UserAdminResponseDto } from '@immich/sdk';
import {
  activateAccount,
  forgetAccount,
  getActiveAccount,
  listAccounts,
  type StoredAccount,
} from './session';

/**
 * The account section at the bottom of the sidebar.
 *
 * Owns account switching and sign-out. Four accounts share this server, so
 * switching is a first-class operation, not a settings page afterthought: the
 * footer is a button, and the popover above it lists every stored account for
 * a one-click switch.
 *
 * The identity DISPLAYED comes from the `me` prop — the server's answer, via
 * the shell — while the switch list comes from the local session store. Kept
 * separate on purpose: the store is a convenience cache of tokens, and if the
 * two ever disagree, the server wins the argument about who you are.
 *
 * A switch is optimistic but verified. We point the SDK at the stored token
 * and probe with `getMyUser()`: if the token still works, one cache clear
 * later the whole app belongs to the other account; if the server has expired
 * it, we drop the dead token and finish the switch at the login form with the
 * email already filled in. Either way nobody gets stranded half-switched.
 */
export function AccountMenu({ me }: { me: UserAdminResponseDto }) {
  const [open, setOpen] = useState(false);
  const [busyEmail, setBusyEmail] = useState<string | null>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const navigate = useNavigate();
  const router = useRouter();
  const queryClient = useQueryClient();

  // Read per render rather than mirrored into state: localStorage only changes
  // through the actions on this very menu, and every action re-renders it.
  const accounts = listAccounts();

  // Close on outside click or Escape. A document listener instead of a scrim
  // element because the sidebar is CSS-transformed, which would make a
  // `fixed` scrim position against the sidebar rather than the viewport.
  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: PointerEvent) => {
      if (!containerRef.current?.contains(event.target as Node)) setOpen(false);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false);
    };
    document.addEventListener('pointerdown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [open]);

  async function onSwitch(account: StoredAccount) {
    if (account.email === me.email) {
      setOpen(false);
      return;
    }

    setBusyEmail(account.email);
    const previous = getActiveAccount();
    activateAccount(account.email);

    try {
      await getMyUser();
    } catch {
      // The stored token has died server-side. Restore the previous account so
      // the current session keeps working, forget the dead token, and let the
      // login form finish the switch — email prefilled, one password to type.
      forgetAccount(account.email);
      if (previous) activateAccount(previous.email);
      setBusyEmail(null);
      setOpen(false);
      router.history.push(
        `/login?email=${encodeURIComponent(account.email)}&redirect=${encodeURIComponent('/photos')}`,
      );
      return;
    }

    // The token works: from here on, every request is the other account's.
    // Clearing the cache is what makes the switch VISIBLE — every mounted
    // query refetches under the new identity.
    queryClient.clear();
    setBusyEmail(null);
    setOpen(false);
    await navigate({ to: '/photos' });
  }

  async function onSignOut() {
    setBusyEmail(me.email);
    try {
      await logout();
    } catch {
      // The server session may already be gone — local cleanup still applies.
    }
    forgetAccount(me.email);
    queryClient.clear();
    await navigate({ to: '/login' });
  }

  return (
    <div ref={containerRef} className="relative border-t border-line p-2">
      {open && (
        <div
          role="menu"
          aria-label="Accounts"
          className="absolute bottom-full left-2 right-2 z-10 mb-1 overflow-hidden rounded-lg border border-line bg-overlay shadow-2xl"
        >
          <p className="px-3 pb-1 pt-2.5 text-[11px] font-medium uppercase tracking-wide text-ink-faint">
            Accounts
          </p>

          {accounts.map((account) => {
            const isActive = account.email === me.email;
            const isBusy = account.email === busyEmail;
            return (
              <button
                key={account.email}
                type="button"
                role="menuitem"
                disabled={busyEmail !== null}
                onClick={() => void onSwitch(account)}
                className="flex w-full items-center gap-2.5 px-3 py-2 text-left hover:bg-raised disabled:opacity-60"
              >
                <AvatarDot name={account.name || account.email} active={isActive} />
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-sm text-ink">{account.name || account.email}</span>
                  <span className="block truncate text-xs text-ink-faint">{account.email}</span>
                </span>
                {isBusy ? (
                  <span className="shrink-0 text-xs text-ink-faint">…</span>
                ) : (
                  isActive && (
                    <svg
                      width="14"
                      height="14"
                      viewBox="0 0 24 24"
                      fill="none"
                      stroke="currentColor"
                      strokeWidth="2.4"
                      className="shrink-0 text-accent"
                      aria-hidden
                    >
                      <path d="m5 13 4 4L19 7" />
                    </svg>
                  )
                )}
              </button>
            );
          })}

          <div className="my-1 border-t border-line-soft" role="separator" />

          <button
            type="button"
            role="menuitem"
            onClick={() => {
              setOpen(false);
              // Signing in while signed in ADDS an account — see LoginPage.
              router.history.push('/login?redirect=%2Fphotos');
            }}
            className="flex w-full items-center gap-2.5 px-3 py-2 text-left text-sm text-ink-muted hover:bg-raised hover:text-ink"
          >
            <span className="grid h-7 w-7 shrink-0 place-items-center rounded-full border border-dashed border-line text-ink-faint">
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden>
                <path d="M12 5v14M5 12h14" />
              </svg>
            </span>
            Add account…
          </button>

          <button
            type="button"
            role="menuitem"
            disabled={busyEmail !== null}
            onClick={() => void onSignOut()}
            className="mb-1 flex w-full items-center gap-2.5 px-3 py-2 text-left text-sm text-ink-muted hover:bg-raised hover:text-danger disabled:opacity-60"
          >
            <span className="grid h-7 w-7 shrink-0 place-items-center text-ink-faint">
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
                <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4" />
                <path d="m16 17 5-5-5-5M21 12H9" />
              </svg>
            </span>
            Sign out
          </button>
        </div>
      )}

      <button
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((current) => !current)}
        className="flex w-full items-center gap-2.5 rounded-md px-2 py-2 text-left hover:bg-raised"
      >
        <AvatarDot name={me.name || me.email} active />
        <span className="min-w-0 flex-1">
          <span className="block truncate text-sm text-ink">{me.name || me.email}</span>
          <span className="block truncate text-xs text-ink-faint">{me.email}</span>
        </span>
        <svg
          width="14"
          height="14"
          viewBox="0 0 24 24"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          className="shrink-0 text-ink-faint"
          aria-hidden
        >
          <path d="m7 15 5 5 5-5M7 9l5-5 5 5" />
        </svg>
      </button>
    </div>
  );
}

/**
 * An initial-letter avatar with a presence dot on the active account.
 *
 * No profile images: they would cost a network request per row for something
 * glanced at, and the stored accounts do not carry image paths anyway. A
 * letter in the accent palette is instantly distinguishable across four
 * accounts, which is the entire job.
 */
function AvatarDot({ name, active }: { name: string; active: boolean }) {
  return (
    <span className="relative grid h-7 w-7 shrink-0 place-items-center rounded-full bg-accent-muted text-xs font-semibold uppercase text-accent">
      {name.slice(0, 1)}
      {active && (
        <span
          className="absolute -bottom-0.5 -right-0.5 h-2.5 w-2.5 rounded-full border-2 border-overlay bg-success"
          aria-hidden
        />
      )}
    </span>
  );
}
