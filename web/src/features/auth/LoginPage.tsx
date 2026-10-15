import { useState, type FormEvent } from 'react';
import { useNavigate, useRouter, useSearch } from '@tanstack/react-router';
import { login } from '@immich/sdk';
import { useQueryClient } from '@tanstack/react-query';
import { recordLogin } from './session';

/**
 * Sign in.
 *
 * The server answers a login with both a session cookie AND an access token.
 * We record the token in the per-account session store (see `session.ts` for
 * the trade-off discussion) so this becomes an "add account" flow for free:
 * signing in while already signed in simply stores one more identity to
 * switch between, rather than replacing the current one.
 *
 * Two query parameters shape the page, both optional:
 *
 * - `?redirect=` is set by the router when an unauthenticated visit bounced
 *   off a protected route, so a successful login can land where the person
 *   was actually going instead of resetting them to /photos.
 * - `?email=` prefills the form. The account menu uses it when a stored
 *   token has expired — the switch finishes here with just a password.
 */
export function LoginPage() {
  const navigate = useNavigate();
  const router = useRouter();
  const queryClient = useQueryClient();

  // The login route deliberately has no `validateSearch` in the route table,
  // so these arrive untyped. They are treated as what they are — untrusted
  // URL input — and validated at the point of use.
  const search = useSearch({ strict: false }) as Record<string, unknown>;
  const redirect =
    // Only same-app paths. An absolute URL here would make ?redirect= an open
    // redirect for anyone crafting a link.
    typeof search.redirect === 'string' && search.redirect.startsWith('/')
      ? search.redirect
      : null;
  const presetEmail = typeof search.email === 'string' ? search.email : '';

  const [email, setEmail] = useState(presetEmail);
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function onSubmit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setPending(true);

    try {
      const response = await login({ loginCredentialDto: { email, password } });
      // Store the token and point the SDK at it before anything refetches.
      recordLogin(response);
      // Drop any cached state from a previous account before entering the app.
      queryClient.clear();
      if (redirect) {
        // The redirect is a raw path, not a known route id, so it goes through
        // history rather than the typed `navigate`.
        router.history.push(redirect);
      } else {
        await navigate({ to: '/photos' });
      }
    } catch {
      // Deliberately vague: distinguishing "no such user" from "wrong password"
      // tells an attacker which half they got right.
      setError('That email and password did not match.');
    } finally {
      setPending(false);
    }
  }

  return (
    <div className="grid h-full place-items-center bg-ground px-4">
      <div className="w-full max-w-sm">
        <div className="mb-8 flex items-center gap-3">
          <span className="grid h-10 w-10 place-items-center rounded-lg bg-accent text-lg font-bold text-ink-inverse">
            P
          </span>
          <div>
            <h1 className="text-lg font-semibold tracking-tight text-ink">Photos</h1>
            <p className="text-sm text-ink-faint">Sign in to your library</p>
          </div>
        </div>

        <form onSubmit={onSubmit} className="space-y-4">
          <Field label="Email" htmlFor="email">
            <input
              id="email"
              type="email"
              autoComplete="username"
              required
              value={email}
              onChange={(event) => setEmail(event.target.value)}
              className="w-full rounded-md border border-line bg-surface px-3 py-2 text-sm text-ink focus:border-accent focus:outline-none"
            />
          </Field>

          <Field label="Password" htmlFor="password">
            <input
              id="password"
              type="password"
              autoComplete="current-password"
              // When the email came prefilled, the password is the only thing
              // left to type — put the cursor there.
              autoFocus={presetEmail.length > 0}
              required
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              className="w-full rounded-md border border-line bg-surface px-3 py-2 text-sm text-ink focus:border-accent focus:outline-none"
            />
          </Field>

          {error && (
            <p role="alert" className="rounded-md bg-danger/10 px-3 py-2 text-sm text-danger">
              {error}
            </p>
          )}

          <button
            type="submit"
            disabled={pending}
            className="w-full rounded-md bg-accent px-4 py-2 text-sm font-semibold text-ink-inverse transition-colors hover:bg-accent-hover disabled:opacity-60"
          >
            {pending ? 'Signing in…' : 'Sign in'}
          </button>
        </form>
      </div>
    </div>
  );
}

function Field({
  label,
  htmlFor,
  children,
}: {
  label: string;
  htmlFor: string;
  children: React.ReactNode;
}) {
  return (
    <div>
      <label htmlFor={htmlFor} className="mb-1.5 block text-sm font-medium text-ink-muted">
        {label}
      </label>
      {children}
    </div>
  );
}
