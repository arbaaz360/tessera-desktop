import { useState } from 'react';
import { Link } from '@tanstack/react-router';
import { formatDateTime } from '@/shared/format';
import {
  apiErrorMessage,
  useAccounts,
  useAcrossDuplicates,
  useDrift,
  useRoots,
  useScanRoot,
  useScans,
  useWithinDuplicates,
  type AdminAccount,
  type ScanRoot,
  type ScanRun,
} from './api';

/**
 * Administration, in Tessera's terms.
 *
 * Immich's admin page managed users with passwords, "external libraries" and
 * a storage template. None of those exist here: an account is a person's
 * library rooted at folders on disk, files arrive by being copied into
 * those folders, and nothing is ever renamed or moved by the system. So the
 * page shows what an owner actually needs to see - which accounts there are,
 * which roots each one watches and when it was last walked, whether the
 * database agrees with the disk, and where the same bytes sit twice.
 */
type Tab = 'accounts' | 'health' | 'duplicates';

const TABS: { id: Tab; label: string }[] = [
  { id: 'accounts', label: 'Accounts and roots' },
  { id: 'health', label: 'Health' },
  { id: 'duplicates', label: 'Duplicates' },
];

export function AdminPage() {
  const [tab, setTab] = useState<Tab>('accounts');

  return (
    <div className="scrollbar-subtle h-full overflow-y-auto">
      <div className="mx-auto max-w-4xl px-4 py-6">
        <h1 className="text-xl font-semibold tracking-tight text-ink">Administration</h1>

        <nav className="mt-4 flex gap-1 border-b border-line" aria-label="Admin sections">
          {TABS.map(({ id, label }) => (
            <button
              key={id}
              type="button"
              onClick={() => setTab(id)}
              aria-current={tab === id ? 'page' : undefined}
              className={`-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors ${
                tab === id
                  ? 'border-accent text-accent'
                  : 'border-transparent text-ink-muted hover:text-ink'
              }`}
            >
              {label}
            </button>
          ))}
        </nav>

        <div className="py-5">
          {tab === 'accounts' && <AccountsSection />}
          {tab === 'health' && <HealthSection />}
          {tab === 'duplicates' && <DuplicatesSection />}
        </div>
      </div>
    </div>
  );
}

/* --------------------------------------------------------------- accounts */

function AccountsSection() {
  const accounts = useAccounts();
  const roots = useRoots();
  const scans = useScans();

  if (accounts.isPending || roots.isPending) return <Placeholder rows={3} />;
  if (accounts.isError) return <ErrorNote error={accounts.error} />;
  if (roots.isError) return <ErrorNote error={roots.error} />;

  const lastScanByLabel = new Map<string, ScanRun>();
  for (const run of scans.data ?? []) {
    if (!lastScanByLabel.has(run.label)) lastScanByLabel.set(run.label, run);
  }

  return (
    <div className="space-y-4">
      <p className="text-sm text-ink-faint">
        An account is a library rooted at folders on disk. Files arrive by being copied into a
        root; the watch scans small folders on its own and lists the rest on{' '}
        <Link to="/jobs" className="text-accent hover:underline">
          Jobs
        </Link>
        . Accounts and roots are created from the command line, not here.
      </p>

      {accounts.data.map((account) => (
        <AccountCard
          key={account.accountId}
          account={account}
          roots={(roots.data ?? []).filter((root) => root.account === account.email)}
          lastScanByLabel={lastScanByLabel}
        />
      ))}
    </div>
  );
}

function AccountCard({
  account,
  roots,
  lastScanByLabel,
}: {
  account: AdminAccount;
  roots: ScanRoot[];
  lastScanByLabel: Map<string, ScanRun>;
}) {
  const scan = useScanRoot();
  const [message, setMessage] = useState<string | null>(null);

  return (
    <section className="rounded-lg border border-line bg-surface p-4">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <h2 className="text-base font-semibold text-ink">{account.displayName}</h2>
        <span className="text-sm text-ink-faint">{account.email}</span>
        <span
          className={`rounded px-1.5 py-0.5 text-[11px] font-medium uppercase tracking-wide ${
            account.isCorpus ? 'bg-raised text-ink-faint' : 'bg-accent-muted text-accent'
          }`}
          title={
            account.isCorpus
              ? 'A corpus: browsed, not curated. No thumbnails or cover suggestions are made for it.'
              : 'A personal library: thumbnails, covers, faces and search.'
          }
        >
          {account.kind}
        </span>
      </div>

      {roots.length === 0 ? (
        <p className="mt-3 text-sm text-ink-faint">No roots.</p>
      ) : (
        <table className="mt-3 w-full text-sm">
          <thead>
            <tr className="text-left text-xs uppercase tracking-wide text-ink-faint">
              <th className="py-1 pr-3 font-medium">Root</th>
              <th className="py-1 pr-3 text-right font-medium">Live files</th>
              <th className="py-1 pr-3 font-medium">Last scan</th>
              <th className="py-1 font-medium" />
            </tr>
          </thead>
          <tbody>
            {roots.map((root) => {
              const last = lastScanByLabel.get(root.label);
              return (
                <tr key={root.id} className="border-t border-line-soft">
                  <td className="py-2 pr-3 font-mono text-[12px] text-ink-muted">{root.abs_path}</td>
                  <td className="py-2 pr-3 text-right tabular-nums text-ink">
                    {root.live_files.toLocaleString()}
                  </td>
                  <td className="py-2 pr-3 text-ink-muted">
                    {last ? (
                      <span title={`${last.files_seen.toLocaleString()} seen · ${last.added} added · ${last.marked_gone} gone · ${last.errors} errors`}>
                        {formatDateTime(last.finished_at ?? last.started_at)}
                        {last.cancelled ? ' (cancelled)' : ''}
                      </span>
                    ) : (
                      <span className="text-ink-faint">never</span>
                    )}
                  </td>
                  <td className="py-2 text-right">
                    <button
                      type="button"
                      disabled={!root.enabled || scan.isPending}
                      onClick={() =>
                        scan.mutate(root.id, {
                          onSuccess: () => setMessage(`Scanning ${root.abs_path}. Progress on Jobs.`),
                          onError: (error) => setMessage(apiErrorMessage(error)),
                        })
                      }
                      title="Walk the whole root now. One scan at a time; small new folders are scanned by the watch without asking."
                      className="rounded-md border border-line px-2.5 py-1 text-[13px] font-medium text-ink-muted transition-colors hover:text-ink disabled:opacity-50"
                    >
                      Scan root
                    </button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}

      {message && <p className="mt-2 text-sm text-ink-muted">{message}</p>}
    </section>
  );
}

/* ----------------------------------------------------------------- health */

function HealthSection() {
  const drift = useDrift();
  if (drift.isPending) return <Placeholder rows={2} />;
  if (drift.isError) return <ErrorNote error={drift.error} />;
  const d = drift.data;

  const rows: { label: string; value: number; note?: string; warn?: boolean }[] = [
    { label: 'Pictures and videos (unique bytes)', value: d.content_rows },
    { label: 'Live files on disk', value: d.live_locations, note: 'the same bytes can sit in several places' },
    { label: 'Timeline rows', value: d.timeline_rows },
    { label: 'Files that have gone missing', value: d.gone_locations, warn: d.gone_locations > 0, note: 'seen once, absent at the last scan' },
    { label: 'In the trash', value: d.trashed },
    { label: 'Thumbnails still to make', value: d.missing_thumbnails, note: 'corpus accounts get none by rule' },
    { label: 'Orphan thumbnails', value: d.orphan_thumbnails, warn: d.orphan_thumbnails > 0 },
    { label: 'Work waiting', value: d.work_pending },
    { label: 'Work failed', value: d.work_failed, warn: d.work_failed > 0 },
  ];

  return (
    <div className="space-y-4">
      <p className="text-sm text-ink-faint">
        Does the database still agree with the disk? These are counted live.
      </p>
      <dl className="grid grid-cols-1 gap-2 sm:grid-cols-2">
        {rows.map((row) => (
          <div key={row.label} className="rounded-lg border border-line bg-surface px-4 py-3">
            <dt className="text-xs uppercase tracking-wide text-ink-faint">{row.label}</dt>
            <dd className={`mt-1 text-lg tabular-nums ${row.warn ? 'text-warn' : 'text-ink'}`}>
              {row.value.toLocaleString()}
            </dd>
            {row.note && <p className="mt-0.5 text-xs text-ink-faint">{row.note}</p>}
          </div>
        ))}
      </dl>
    </div>
  );
}

/* ------------------------------------------------------------- duplicates */

function DuplicatesSection() {
  const drift = useDrift();
  const within = useWithinDuplicates();
  const across = useAcrossDuplicates();

  return (
    <div className="space-y-6">
      <p className="text-sm text-ink-faint">
        The same bytes filed more than once. Nothing here is deleted by the system: within one
        account a twin is waste you may remove by hand; across accounts it may be deliberate.
        {drift.data && (
          <>
            {' '}
            {drift.data.duplicate_sets_in_account.toLocaleString()} sets within accounts,{' '}
            {drift.data.duplicate_sets_across_accounts.toLocaleString()} across.
          </>
        )}
      </p>

      <section>
        <h2 className="text-sm font-semibold text-ink">Within one account - largest sets first</h2>
        {within.isPending ? (
          <Placeholder rows={3} />
        ) : within.isError ? (
          <ErrorNote error={within.error} />
        ) : (
          <ul className="mt-2 space-y-2">
            {within.data.map((set) => (
              <li key={`${set.account_id}-${set.content_id}`} className="rounded-lg border border-line bg-surface p-3">
                <div className="flex items-baseline gap-3 text-sm">
                  <span className="tabular-nums text-ink">{set.copies} copies</span>
                  <span className="text-ink-faint">account {set.account_id}</span>
                  <Link
                    to="/photos/$assetId"
                    params={{ assetId: assetIdFor(set.content_id) }}
                    className="ml-auto text-accent hover:underline"
                  >
                    open
                  </Link>
                </div>
                <ul className="mt-1 max-h-24 overflow-y-auto font-mono text-[11px] text-ink-faint">
                  {set.paths.slice(0, 12).map((path) => (
                    <li key={path}>{path}</li>
                  ))}
                  {set.paths.length > 12 && <li>… {set.paths.length - 12} more</li>}
                </ul>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section>
        <h2 className="text-sm font-semibold text-ink">Across accounts</h2>
        {across.isPending ? (
          <Placeholder rows={2} />
        ) : across.isError ? (
          <ErrorNote error={across.error} />
        ) : (
          <table className="mt-2 w-full text-sm">
            <thead>
              <tr className="text-left text-xs uppercase tracking-wide text-ink-faint">
                <th className="py-1 pr-3 font-medium">Accounts</th>
                <th className="py-1 pr-3 text-right font-medium">Copies</th>
                <th className="py-1 font-medium" />
              </tr>
            </thead>
            <tbody>
              {across.data.map((set) => (
                <tr key={set.content_id} className="border-t border-line-soft">
                  <td className="py-1.5 pr-3 text-ink-muted">{set.accounts.join(', ')}</td>
                  <td className="py-1.5 pr-3 text-right tabular-nums text-ink">{set.copies}</td>
                  <td className="py-1.5 text-right">
                    <Link
                      to="/photos/$assetId"
                      params={{ assetId: assetIdFor(set.content_id) }}
                      className="text-accent hover:underline"
                    >
                      open
                    </Link>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}

/** Tessera's content id in the asset-id spelling the viewer routes on. */
function assetIdFor(contentId: number): string {
  return `00000000-0000-4000-8000-${contentId.toString(16).padStart(12, '0')}`;
}

/* ----------------------------------------------------------------- shared */

function Placeholder({ rows }: { rows: number }) {
  return (
    <div className="space-y-3">
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className="h-12 animate-pulse rounded-lg bg-raised" />
      ))}
    </div>
  );
}

function ErrorNote({ error }: { error: unknown }) {
  return <p className="text-sm text-warn">{apiErrorMessage(error)}</p>;
}
