import { setUrlSessionKey } from '@/shared/api/client';
import { defaults, type LoginResponseDto } from '@immich/sdk';

/**
 * Per-account session tokens, persisted in localStorage.
 *
 * Immich issues one session cookie per BROWSER, but this deployment has four
 * people sharing one screen. A cookie can only remember the most recent login,
 * so instant account switching needs something the cookie cannot give us: one
 * credential per account, selectable at will. The login response carries an
 * `accessToken` for exactly this, and the server accepts it as
 * `Authorization: Bearer <token>`.
 *
 * THE TRADE-OFF, stated plainly rather than hidden: a token in localStorage is
 * readable by any JavaScript running on this origin, which is why the
 * cookie-only stance existed in the first place. The owner accepts that here
 * deliberately — this app is served on a LAN to trusted machines, there is no
 * third-party script on the page, and the alternative (re-typing a password on
 * every switch) fails the actual requirement. On a public deployment this file
 * is the first thing to revisit.
 *
 * Two facts about the server, verified rather than assumed:
 *
 * - PRECEDENCE. In the fork's `auth.service.ts` `validate()`, the bearer token
 *   is consulted BEFORE the cookie. So once we set the header, a stale cookie
 *   from an earlier login cannot shadow the active account. (Probed live: a
 *   garbage bearer yields 401 "Invalid user token" even alongside a valid
 *   cookie — the bearer path really does win.)
 *
 * - IMAGES. `<img>` tags cannot send headers, so on their own thumbnails would
 *   ride the session cookie — which belongs to whoever logged in last. The
 *   server accepts a `?sessionKey=` query parameter ahead of the cookie, and
 *   `applyAuthHeader` below keeps the URL builders in `shared/api/client.ts`
 *   supplied with the active token, so image bytes and API data always answer
 *   as the same account.
 */

export interface StoredAccount {
  email: string;
  name: string;
  userId: string;
  token: string;
}

export interface SessionStore {
  accounts: StoredAccount[];
  /** Which account's token is currently applied. Null means cookie-only. */
  activeEmail: string | null;
}

const STORAGE_KEY = 'photos-web.auth.accounts';

const EMPTY_STORE: SessionStore = { accounts: [], activeEmail: null };

/**
 * Parse a stored value back into a session store, defensively.
 *
 * Same discipline as the albums view prefs: localStorage survives deploys and
 * is user-writable, so whatever is in there is INPUT, not state we control.
 * Accounts missing any field are dropped individually — one corrupt entry must
 * not sign everyone out — and `activeEmail` is only honoured if it still names
 * an account that survived.
 */
export function parseSessionStore(raw: string | null): SessionStore {
  if (!raw) return EMPTY_STORE;

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return EMPTY_STORE;
  }
  if (typeof parsed !== 'object' || parsed === null) return EMPTY_STORE;

  const candidate = parsed as Record<string, unknown>;
  const accounts = Array.isArray(candidate.accounts)
    ? candidate.accounts.filter(isStoredAccount)
    : [];

  const activeEmail =
    typeof candidate.activeEmail === 'string' &&
    accounts.some((account) => account.email === candidate.activeEmail)
      ? candidate.activeEmail
      : null;

  return { accounts, activeEmail };
}

function isStoredAccount(value: unknown): value is StoredAccount {
  if (typeof value !== 'object' || value === null) return false;
  const candidate = value as Record<string, unknown>;
  return (
    typeof candidate.email === 'string' &&
    candidate.email.length > 0 &&
    typeof candidate.name === 'string' &&
    typeof candidate.userId === 'string' &&
    typeof candidate.token === 'string' &&
    candidate.token.length > 0
  );
}

function readStore(): SessionStore {
  try {
    return parseSessionStore(localStorage.getItem(STORAGE_KEY));
  } catch {
    // Storage blocked (private window) degrades to cookie-only auth.
    return EMPTY_STORE;
  }
}

function writeStore(store: SessionStore): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(store));
  } catch {
    // Losing persistence must never take sign-in down with it.
  }
}

/**
 * Point the SDK at a token — or back at the cookie.
 *
 * `defaults` is the oazapfts request-defaults object every generated SDK call
 * reads at request time, so mutating it here changes ALL subsequent requests
 * without re-initialising anything. Deleting the header (rather than setting
 * it to an empty string) matters: oazapfts drops only null/undefined values
 * when building headers, and an empty `Authorization:` would still be sent —
 * and rejected.
 */
function applyAuthHeader(token: string | null): void {
  if (token) {
    defaults.headers.Authorization = `Bearer ${token}`;
  } else {
    delete defaults.headers.Authorization;
  }
  // Image and video URLs cannot carry this header, so the client's URL
  // builders append the same token as ?sessionKey= — see setUrlSessionKey in
  // shared/api/client.ts for why. Kept in lockstep here so the two auth paths
  // can never answer as different accounts.
  setUrlSessionKey(token);
}

export function listAccounts(): StoredAccount[] {
  return readStore().accounts;
}

export function getActiveAccount(): StoredAccount | null {
  const store = readStore();
  return store.accounts.find((account) => account.email === store.activeEmail) ?? null;
}

/**
 * Record a successful login and make it the active account.
 *
 * Upsert by email: logging into an account we already know refreshes its
 * token rather than duplicating the entry. Called from the login page, which
 * is the ONLY place tokens enter this store — everything else just selects
 * among them.
 */
export function recordLogin(login: LoginResponseDto): StoredAccount {
  const account: StoredAccount = {
    email: login.userEmail,
    name: login.name,
    userId: login.userId,
    token: login.accessToken,
  };

  const store = readStore();
  const others = store.accounts.filter((existing) => existing.email !== account.email);
  writeStore({ accounts: [...others, account], activeEmail: account.email });
  applyAuthHeader(account.token);
  return account;
}

/**
 * Switch the SDK to a stored account's token. Returns the account, or null if
 * the email is unknown — the caller decides what "unknown" means for its UI.
 *
 * Deliberately does NOT clear the query cache or navigate: this module knows
 * about storage and headers, not about React. The component driving the
 * switch owns those consequences, where it also has access to the query
 * client and router.
 */
export function activateAccount(email: string): StoredAccount | null {
  const store = readStore();
  const account = store.accounts.find((existing) => existing.email === email);
  if (!account) return null;

  writeStore({ ...store, activeEmail: account.email });
  applyAuthHeader(account.token);
  return account;
}

/**
 * Drop a stored account — on sign-out, or when its token turns out to be dead.
 * If it was the active one, the SDK falls back to cookie auth.
 */
export function forgetAccount(email: string): void {
  const store = readStore();
  const wasActive = store.activeEmail === email;
  writeStore({
    accounts: store.accounts.filter((existing) => existing.email !== email),
    activeEmail: wasActive ? null : store.activeEmail,
  });
  if (wasActive) applyAuthHeader(null);
}

/**
 * Boot-time wiring, as a module side effect.
 *
 * The router's `beforeLoad` calls `getMyUser()` to gate every protected route,
 * so the bearer header must be in place before that first request. This module
 * is imported by `LoginPage` and `AccountMenu`, both of which sit in the
 * router's import graph — and ES modules evaluate their whole import graph
 * before any application code runs. Executing here therefore guarantees the
 * header exists before the first render, without this feature needing to own
 * a line of the boot file.
 */
applyAuthHeader(getActiveAccount()?.token ?? null);
