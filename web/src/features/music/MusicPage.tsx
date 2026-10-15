import { useState } from 'react';
import { useMusicScan, useMusicStatus, useMusicToggle, type MusicStatus } from './api';
import { clampPercent, formatWhen, recognitionHeadline } from './format';
import { IdentifyUpload } from './IdentifyUpload';
import { TrackDetail } from './TrackDetail';

/**
 * Music: the recognition dashboard. After the cutover this page is the ONLY
 * interface to the sidecar on :2298 — the injected overlay in the old Svelte
 * web died with it.
 *
 * The layout mirrors what the service actually is: two independent pipelines
 * over the same videos. Shazam-style RECOGNITION names the track playing in a
 * video (needs the identifier routes, i.e. the internet); the local
 * FINGERPRINT index powers "find similar audio" entirely offline. Each gets
 * its own section with its own counters and queue controls, because pausing
 * one has no effect on the other.
 *
 * What you will NOT find here is a browse-all-tracks list. The sidecar has no
 * endpoint that enumerates tracks — by design, its reads are per-asset and
 * per-track-key — so tracks are reached through a video's now-playing panel
 * or by identifying a file below. Building a fake "library" view over an API
 * that cannot list would mean crawling every video from the client.
 *
 * Track selection is page-local `useState` rather than the URL: this route
 * declares no search params, and a track key is a hash with no meaning to
 * share. If deep-linking a track ever matters, the router owns that change.
 */
export function MusicPage() {
  const [trackKey, setTrackKey] = useState<string | null>(null);

  // Drill-in, PeoplePage-style: the detail replaces the dashboard instead of
  // stacking a modal on top of it. One screen, one job.
  if (trackKey) {
    return <TrackDetail trackKey={trackKey} onBack={() => setTrackKey(null)} />;
  }

  return <Dashboard onOpenTrack={setTrackKey} />;
}

function Dashboard({ onOpenTrack }: { onOpenTrack: (trackKey: string) => void }) {
  const status = useMusicStatus();

  return (
    <div className="scrollbar-subtle h-full overflow-y-auto">
      <div className="mx-auto max-w-5xl space-y-8 px-6 py-6">
        <header>
          <h1 className="text-lg font-semibold tracking-tight text-ink">Music</h1>
          <p className="mt-0.5 text-sm text-ink-muted">
            Tracks identified in your videos, and the queues that find them.
          </p>
        </header>

        {status.isPending && <DashboardSkeleton />}

        {/* The one quiet notice for a dead sidecar. The status query keeps
            polling slowly behind it, so recovery needs no reload — the page
            simply comes back. */}
        {status.isError && (
          <p className="rounded-lg border border-line bg-surface px-4 py-3 text-sm text-ink-muted">
            Could not load the music status. It will reappear here on its own once Tessera answers.
          </p>
        )}

        {status.data && (
          <>
            <RecognitionSection status={status.data} />

            <section className="space-y-3">
              <SectionHeading
                title="Identify a file"
                subtitle="Shazam any video or audio file, then jump to the library videos sharing its track."
              />
              <IdentifyUpload onOpenTrack={onOpenTrack} />
            </section>

            <p className="text-xs text-ink-faint">
              Similar-audio search is not available yet: its index lived in the retired service and
              gets its own contract.
            </p>
          </>
        )}
      </div>
    </div>
  );
}

/* ----------------------------------------------------------- recognition */

function RecognitionSection({ status }: { status: MusicStatus }) {
  const scan = useMusicScan();
  const toggle = useMusicToggle();
  const busy = scan.isPending || toggle.isPending;

  const headline = recognitionHeadline({
    paused: status.paused,
    identifierAvailable: status.identifierAvailable,
    processing: status.processing,
    pending: status.pending,
  });

  return (
    <section className="space-y-3">
      <SectionHeading
        title="Track recognition"
        subtitle={`Identifier ${status.identifierAvailable ? 'available' : 'unreachable'} · last answered ${formatWhen(status.lastIdentifiedAt)}`}
      />

      <div className="grid gap-3" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(130px, 1fr))' }}>
        <StatTile label="Videos" value={status.videos} />
        <StatTile label="Matched" value={status.matched} />
        <StatTile label="No match" value={status.noMatch} />
        <StatTile label="Never asked" value={status.never} />
        <StatTile label="Pending" value={status.pending} />
        <StatTile label="Processing" value={status.processing} />
        <StatTile label="Failed" value={status.failed} />
      </div>

      <Progress percent={status.progressPercent} headline={headline} />

      {!status.identifierAvailable && (
        <p className="text-sm text-danger">
          The identifier is not reachable. The queue is kept and continues when it is back.
        </p>
      )}

      <div className="flex flex-wrap items-center gap-2">
        <ActionButton tone="primary" disabled={busy} onClick={() => scan.mutate('missing')}>
          Ask about new videos
        </ActionButton>
        <ActionButton
          tone="neutral"
          disabled={busy}
          onClick={() => toggle.mutate(status.paused ? 'resume' : 'pause')}
        >
          {status.paused ? 'Resume' : 'Pause'}
        </ActionButton>
        <ActionButton tone="neutral" disabled={busy} onClick={() => scan.mutate('no_match')}>
          Retry no matches
        </ActionButton>
        <ActionButton tone="neutral" disabled={busy} onClick={() => scan.mutate('failed')}>
          Retry failures
        </ActionButton>
        {/* `confirm` over a custom dialog: "ask again about everything" re-spends
            hours of identifier calls. Proportionate friction for a once-a-year
            action. Existing answers stay until the new one lands. */}
        <ActionButton
          tone="danger"
          disabled={busy}
          onClick={() => {
            if (window.confirm('Queue every video for recognition again? Each answer is replaced when the new one arrives.')) {
              scan.mutate('all');
            }
          }}
        >
          Ask again about everything
        </ActionButton>
      </div>

      <MutationNote
        error={scan.error ?? toggle.error}
        queued={scan.data?.queued}
        verb="queued for recognition"
      />
    </section>
  );
}

function SectionHeading({ title, subtitle }: { title: string; subtitle: string }) {
  return (
    <div>
      <h2 className="text-base font-semibold tracking-tight text-ink">{title}</h2>
      <p className="mt-0.5 text-xs text-ink-faint">{subtitle}</p>
    </div>
  );
}

function StatTile({ label, value }: { label: string; value: number }) {
  return (
    <div className="rounded-lg border border-line bg-surface px-3.5 py-3">
      <p className="text-xs text-ink-faint">{label}</p>
      <p className="mt-1 text-xl font-semibold tabular-nums text-ink">{value.toLocaleString()}</p>
    </div>
  );
}

function Progress({ percent, headline }: { percent: number; headline: string }) {
  const clamped = clampPercent(percent);
  return (
    <div className="space-y-1.5">
      <div className="flex items-baseline justify-between gap-3">
        <p className="text-sm text-ink-muted">{headline}</p>
        <p className="text-sm tabular-nums text-ink-faint">{clamped.toFixed(1)}%</p>
      </div>
      <div
        className="h-1.5 overflow-hidden rounded-full bg-raised"
        role="progressbar"
        aria-valuenow={Math.round(clamped)}
        aria-valuemin={0}
        aria-valuemax={100}
      >
        <div
          className="h-full rounded-full bg-accent transition-[width] duration-500"
          style={{ width: `${clamped}%`, transitionTimingFunction: 'var(--ease-out-soft)' }}
        />
      </div>
    </div>
  );
}

const BUTTON_TONES = {
  primary: 'bg-accent text-ink-inverse hover:bg-accent-hover',
  neutral: 'border border-line bg-surface text-ink-muted hover:text-ink',
  danger: 'border border-line bg-surface text-danger hover:border-danger',
} as const;

function ActionButton({
  tone,
  disabled,
  onClick,
  children,
}: {
  tone: keyof typeof BUTTON_TONES;
  disabled: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      disabled={disabled}
      onClick={onClick}
      className={`rounded-md px-3 py-1.5 text-sm font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${BUTTON_TONES[tone]}`}
    >
      {children}
    </button>
  );
}

/**
 * The one-line aftermath of a queue action: an error, or how much work it
 * queued. Deliberately not a toast — the numbers above already move within a
 * poll cycle, so this only needs to catch the eye for a moment.
 */
function MutationNote({
  error,
  queued,
  verb,
}: {
  error: Error | null;
  queued: number | undefined;
  verb: string;
}) {
  if (error) return <p className="text-sm text-danger">{error.message}</p>;
  if (queued === undefined) return null;
  return (
    <p className="text-sm text-ink-faint">
      {queued.toLocaleString()} {queued === 1 ? 'video' : 'videos'} {verb}.
    </p>
  );
}

function DashboardSkeleton() {
  return (
    <div className="space-y-8">
      {Array.from({ length: 2 }, (_, section) => (
        <div key={section} className="space-y-3">
          <div className="h-4 w-40 animate-pulse rounded bg-raised" />
          <div className="grid gap-3" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(130px, 1fr))' }}>
            {Array.from({ length: 4 }, (_, index) => (
              <div
                key={index}
                className="h-[74px] animate-pulse rounded-lg bg-raised"
                style={{ animationDelay: `${(section * 4 + index) * 35}ms` }}
              />
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}
