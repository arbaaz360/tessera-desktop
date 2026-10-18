import { useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useLocation } from '@tanstack/react-router';
import { appVersion, licenceActivate, licenceGet, openExternal, pickFolder, type ShellLicence } from '@/shared/api/shell';
import { isTauri } from '@/shared/api/core';
import { apiErrorMessage } from '@/shared/api/tessera';
import { qk } from '@/shared/api/queries';
import { formatDateTime } from '@/shared/format';
import {
  useLicence,
  useSearchStatus,
  useSetSearchEnabled,
  useSettings,
  useUpdateSettings,
} from '@/features/library/api';
import { MODEL_STATE_LABEL, modelState } from '@/features/library/modelState';
import { BUY_URL } from './licence';

/**
 * Settings: the few knobs the core exposes, what version this is, and the
 * licence.
 *
 * Every control commits on its own - there is no Save button - because each
 * is a complete choice and the core answers with the full settings, which
 * replace the cache. The licence section is reachable by hash (`#licence`)
 * from the free-tier banners, so it scrolls into view when asked for.
 */
export function SettingsPage() {
  const { hash } = useLocation();

  useEffect(() => {
    if (hash === 'licence') {
      document.getElementById('licence')?.scrollIntoView({ block: 'start' });
    }
  }, [hash]);

  return (
    <div className="scrollbar-subtle h-full overflow-y-auto">
      <div className="mx-auto max-w-3xl px-4 py-6">
        <header className="mb-5">
          <h1 className="text-xl font-semibold tracking-tight text-ink">Settings</h1>
        </header>

        <div className="space-y-6">
          <GeneralSection />
          <SearchSection />
          <LicenceSection />
          <AboutSection />
        </div>
      </div>
    </div>
  );
}

function GeneralSection() {
  const settings = useSettings();
  const update = useUpdateSettings();

  return (
    <Section title="Thumbnails and indexing">
      {settings.isPending && <p className="text-sm text-ink-faint">Loading…</p>}
      {settings.isError && <p className="text-sm text-danger">{apiErrorMessage(settings.error)}</p>}
      {settings.data && (
        <>
          <Field
            label="Thumbnail workers"
            hint="How many pictures are decoded at once. More is faster and uses more of the machine."
          >
            <ConcurrencyInput
              value={settings.data.thumbnailConcurrency}
              onCommit={(value) => update.mutate({ thumbnailConcurrency: value })}
            />
          </Field>
          <Field label="Index on battery" hint="Keep building thumbnails and search vectors while unplugged.">
            <Toggle
              checked={settings.data.embedOnBattery}
              onChange={(embedOnBattery) => update.mutate({ embedOnBattery })}
              label="Index on battery"
            />
          </Field>
          <TesseraFolderField path={settings.data.tesseraFolder} />
          {update.isError && <p className="text-xs text-danger">{apiErrorMessage(update.error)}</p>}
        </>
      )}
    </Section>
  );
}

function SearchSection() {
  const status = useSearchStatus();
  const setEnabled = useSetSearchEnabled();

  return (
    <Section title="Search by meaning">
      {status.isPending && <p className="text-sm text-ink-faint">Loading…</p>}
      {status.isError && <p className="text-sm text-danger">{apiErrorMessage(status.error)}</p>}
      {status.data && (
        <>
          <Field
            label="Search enabled"
            hint="Builds a vector for every picture so you can find them by describing them. Turning it off stops the indexing; the vectors already built are kept."
          >
            <Toggle
              checked={status.data.enabled}
              onChange={(enabled) => setEnabled.mutate(enabled)}
              label="Search enabled"
            />
          </Field>
          <dl className="grid grid-cols-2 gap-x-6 gap-y-2 text-sm sm:grid-cols-4">
            <Info label="Model">{status.data.model}</Info>
            <Info label="Runs on">{status.data.ready ? status.data.device : '—'}</Info>
            <Info label="Indexed">
              <span className="tabular-nums">
                {status.data.embedded.toLocaleString()} / {status.data.total.toLocaleString()}
              </span>
            </Info>
            <Info label="State">{MODEL_STATE_LABEL[modelState(status.data)]}</Info>
          </dl>
          {setEnabled.isError && <p className="text-xs text-danger">{apiErrorMessage(setEnabled.error)}</p>}
        </>
      )}
    </Section>
  );
}

/**
 * The licence. Two sources agree here: the core's `/api/licence` (the state
 * that gates indexing, and the counts) and the shell's keychain
 * (`licence_get`: the masked key and when it was last checked). Activation
 * goes through the shell, which verifies with Gumroad and tells the core.
 */
function LicenceSection() {
  const licence = useLicence();
  const queryClient = useQueryClient();
  const [shell, setShell] = useState<ShellLicence | null>(null);
  const [key, setKey] = useState('');

  useEffect(() => {
    let live = true;
    licenceGet()
      .then((answer) => {
        if (live) setShell(answer);
      })
      .catch(() => undefined);
    return () => {
      live = false;
    };
  }, []);

  const activate = useMutation({
    mutationFn: (licenceKey: string) => licenceActivate(licenceKey),
    onSuccess: async (answer) => {
      if (answer.error === null) {
        setKey('');
        setShell(await licenceGet().catch(() => shell));
      }
      await queryClient.invalidateQueries({ queryKey: qk.licence });
      await queryClient.invalidateQueries({ queryKey: qk.library.all });
    },
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (key.trim()) activate.mutate(key.trim());
  };

  const licensed = licence.data?.state === 'licensed';

  return (
    <Section id="licence" title="Licence">
      {licence.isPending && <p className="text-sm text-ink-faint">Loading…</p>}
      {licence.isError && <p className="text-sm text-danger">{apiErrorMessage(licence.error)}</p>}
      {licence.data && (
        <div className="flex flex-wrap items-center gap-3">
          <span
            className={`rounded-md px-2 py-0.5 text-xs font-semibold uppercase tracking-wide ${
              licensed ? 'bg-success/15 text-success' : 'bg-accent-muted text-accent'
            }`}
          >
            {licensed ? 'Licensed' : 'Free'}
          </span>
          <p className="text-sm tabular-nums text-ink-muted">
            {licensed
              ? `${licence.data.indexed.toLocaleString()} pictures indexed, no limit.`
              : `${licence.data.indexed.toLocaleString()} pictures found · the free tier indexes the first ${licence.data.cap.toLocaleString()}.`}
          </p>
          {shell?.keyMasked && (
            <p className="text-xs text-ink-faint">
              Key {shell.keyMasked}
              {shell.checkedAt ? ` · checked ${formatDateTime(shell.checkedAt)}` : ''}
            </p>
          )}
        </div>
      )}

      {!licensed && (
        <p className="text-sm text-ink-muted">
          One licence, once, for this machine. Unlocking releases every picture beyond the cap to
          the thumbnail and search queues; nothing is rescanned.
        </p>
      )}

      <form onSubmit={submit} className="flex flex-wrap items-center gap-2">
        <input
          type="text"
          value={key}
          onChange={(event) => setKey(event.target.value)}
          placeholder={licensed ? 'Enter a different key' : 'Licence key'}
          aria-label="Licence key"
          autoComplete="off"
          spellCheck={false}
          className="w-72 rounded-md border border-line bg-surface px-3 py-1.5 font-mono text-sm text-ink placeholder:font-sans placeholder:text-ink-faint focus:border-accent focus:outline-none"
        />
        <button
          type="submit"
          disabled={!key.trim() || activate.isPending}
          className="rounded-md bg-accent px-3 py-1.5 text-sm font-semibold text-ink-inverse transition-colors hover:bg-accent-hover disabled:opacity-60"
        >
          {activate.isPending ? 'Checking…' : 'Activate'}
        </button>
        {!licensed && (
          <button
            type="button"
            onClick={() => void openExternal(BUY_URL)}
            className="rounded-md border border-line px-3 py-1.5 text-sm font-medium text-ink-muted transition-colors hover:bg-raised hover:text-ink"
          >
            Buy a licence
          </button>
        )}
      </form>

      {activate.data && activate.data.error === null && (
        <p className="text-sm text-success" role="status">
          Licence activated. Indexing continues past the cap.
        </p>
      )}
      {activate.data?.error === 'invalid' && (
        <p className="text-sm text-danger" role="alert">
          That key was not recognised. Check it against your purchase receipt.
        </p>
      )}
      {activate.data?.error === 'offline' && (
        <p className="text-sm text-danger" role="alert">
          Could not reach the licence server. Connect to the internet once to activate; the app
          needs no connection afterwards.
        </p>
      )}
      {activate.isError && <p className="text-sm text-danger">{apiErrorMessage(activate.error)}</p>}
    </Section>
  );
}

/**
 * Where pictures from outside the library are copied: the one folder the app
 * writes to. Moving it changes where the next copies go; what was copied
 * before stays where it is, and stays in the library.
 */
function TesseraFolderField({ path }: { path?: string | undefined }) {
  const client = useQueryClient();
  const move = useUpdateSettings();

  const choose = async () => {
    const folder = isTauri()
      ? await pickFolder()
      : window.prompt('The full path of the new Tessera folder', path ?? '')?.trim() || null;
    if (!folder) return;
    move.mutate(
      { tesseraFolder: folder },
      { onSuccess: () => void client.invalidateQueries({ queryKey: qk.library.all }) },
    );
  };

  return (
    <div className="space-y-1">
      <Field
        label="Tessera folder"
        hint="Pictures dropped or picked from outside your folders are copied here, one folder per album. Moving it changes where the next ones go; nothing already copied moves."
      >
        <button
          type="button"
          onClick={() => void choose()}
          disabled={move.isPending}
          className="rounded-md border border-line px-3 py-1.5 text-sm text-ink-muted transition-colors hover:bg-raised hover:text-ink disabled:opacity-50"
        >
          {move.isPending ? 'Moving…' : 'Move…'}
        </button>
      </Field>
      <p className="break-all font-mono text-[12px] text-ink-faint">{path ?? '—'}</p>
      {move.isError && <p className="text-xs text-danger">{apiErrorMessage(move.error)}</p>}
    </div>
  );
}

function AboutSection() {
  const settings = useSettings();
  const [shellVersion, setShellVersion] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    appVersion()
      .then((version) => {
        if (live) setShellVersion(version);
      })
      .catch(() => undefined);
    return () => {
      live = false;
    };
  }, []);

  return (
    <Section title="About">
      <dl className="grid grid-cols-1 gap-y-2 text-sm sm:grid-cols-2">
        <Info label="App">{shellVersion ?? '—'}</Info>
        <Info label="Engine">{settings.data?.version ?? '—'}</Info>
        <div className="sm:col-span-2">
          <Info label="Data folder">
            <span className="break-all font-mono text-[12px]">{settings.data?.dataDir ?? '—'}</span>
          </Info>
        </div>
      </dl>
      <p className="text-xs text-ink-faint">
        Tessera talks to the internet for three things only: downloading the search model, checking
        a licence key, and looking for updates - and, when you drop a link from a browser onto the
        window, fetching that one picture. Your pictures never leave this machine.
      </p>
    </Section>
  );
}

// ------------------------------------------------------------------ pieces

function Section({ id, title, children }: { id?: string; title: string; children: ReactNode }) {
  return (
    <section id={id} aria-label={title} className="space-y-3 rounded-lg border border-line bg-surface p-5">
      <h2 className="text-sm font-semibold text-ink">{title}</h2>
      {children}
    </section>
  );
}

function Field({ label, hint, children }: { label: string; hint: string; children: ReactNode }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div className="min-w-0 max-w-md">
        <p className="text-sm text-ink">{label}</p>
        <p className="text-xs text-ink-faint">{hint}</p>
      </div>
      {children}
    </div>
  );
}

function Info({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <dt className="text-[11px] uppercase tracking-wide text-ink-faint">{label}</dt>
      <dd className="text-ink">{children}</dd>
    </div>
  );
}

/** A switch, as a real checkbox under CSS. */
function Toggle({
  checked,
  onChange,
  label,
}: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  label: string;
}) {
  return (
    <label className="relative inline-flex cursor-pointer items-center">
      <input
        type="checkbox"
        role="switch"
        checked={checked}
        aria-checked={checked}
        aria-label={label}
        onChange={(event) => onChange(event.target.checked)}
        className="peer sr-only"
      />
      <span className="h-6 w-10 rounded-full bg-raised ring-1 ring-line transition-colors peer-checked:bg-accent peer-focus-visible:ring-2 peer-focus-visible:ring-accent" />
      <span className="absolute left-1 top-1 h-4 w-4 rounded-full bg-ink-muted transition-transform peer-checked:translate-x-4 peer-checked:bg-ink-inverse" />
    </label>
  );
}

const CONCURRENCY_MIN = 1;
const CONCURRENCY_MAX = 16;

/** A number that commits on blur or Enter, clamped to what the core accepts. */
function ConcurrencyInput({ value, onCommit }: { value: number; onCommit: (value: number) => void }) {
  const [draft, setDraft] = useState(String(value));

  useEffect(() => setDraft(String(value)), [value]);

  const commit = () => {
    const parsed = Math.round(Number(draft));
    if (!Number.isFinite(parsed)) {
      setDraft(String(value));
      return;
    }
    const clamped = Math.min(CONCURRENCY_MAX, Math.max(CONCURRENCY_MIN, parsed));
    setDraft(String(clamped));
    if (clamped !== value) onCommit(clamped);
  };

  return (
    <input
      type="number"
      min={CONCURRENCY_MIN}
      max={CONCURRENCY_MAX}
      value={draft}
      onChange={(event) => setDraft(event.target.value)}
      onBlur={commit}
      onKeyDown={(event) => {
        if (event.key === 'Enter') event.currentTarget.blur();
      }}
      aria-label="Thumbnail workers"
      className="w-20 rounded-md border border-line bg-surface px-2 py-1.5 text-sm tabular-nums text-ink focus:border-accent focus:outline-none"
    />
  );
}
