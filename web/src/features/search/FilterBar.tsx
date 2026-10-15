import { MediaTypeFilter, type MediaTypeValue } from '@/shared/ui/MediaTypeFilter';
import { countActiveFilters, type SearchFilters } from './searchParams';

/**
 * The filter bar under the search results header.
 *
 * Purely presentational: it receives the current filters and reports the next
 * ones. The page owns writing them to the URL, so this component never touches
 * the router — which also makes each control here trivially reusable.
 *
 * Only the filters this library has: kind of file and the taken date. Camera,
 * place and person were Immich's EXIF and face tables; Tessera keeps neither,
 * and a filter over data that does not exist is a way to get zero results.
 * Every control commits immediately - each is a complete choice.
 */
export function FilterBar({
  filters,
  onChange,
}: {
  filters: SearchFilters;
  onChange: (next: SearchFilters) => void;
}) {
  const activeCount = countActiveFilters(filters);

  // A helper so each control reads as "patch this one field". Controls clear a
  // field by passing `undefined`, but under `exactOptionalPropertyTypes` a
  // SearchFilters object may not CONTAIN an undefined value — the key must be
  // absent. So the patch type explicitly admits undefined, and the merge
  // deletes those keys instead of spreading them. "Cleared" and "never set"
  // end up as the same object shape, which is what the URL serialiser and the
  // filter counter both assume.
  const patch = (partial: { [K in keyof SearchFilters]?: SearchFilters[K] | undefined }) => {
    const next: Record<string, unknown> = { ...filters };
    for (const [key, value] of Object.entries(partial)) {
      if (value === undefined) delete next[key];
      else next[key] = value;
    }
    onChange(next as unknown as SearchFilters);
  };

  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-2 border-b border-line-soft px-4 py-2.5">
      <MediaTypeFilter
        value={(filters.type ?? 'ALL') as MediaTypeValue}
        onChange={(value) => patch({ type: value === 'ALL' ? undefined : value })}
      />

      <div className="inline-flex items-center gap-1.5">
        <DateField
          label="After"
          value={filters.takenAfter ?? ''}
          onChange={(value) => patch({ takenAfter: value || undefined })}
        />
        <span className="text-ink-faint" aria-hidden>
          –
        </span>
        <DateField
          label="Before"
          value={filters.takenBefore ?? ''}
          onChange={(value) => patch({ takenBefore: value || undefined })}
        />
      </div>

      {/* The mode toggle. A checkbox under the hood — it IS a boolean choice —
          styled as a pill so it reads as a peer of the other filters. */}
      <label
        className={`inline-flex cursor-pointer items-center gap-1.5 rounded-lg border px-2.5 py-1.5 text-[13px] font-medium transition-colors ${
          filters.metadataOnly
            ? 'border-accent bg-accent-muted text-accent'
            : 'border-line bg-surface text-ink-muted hover:text-ink'
        }`}
        title="Match the file name and the filters exactly, instead of ranking by what is in the picture."
      >
        <input
          type="checkbox"
          checked={filters.metadataOnly}
          onChange={(event) => patch({ metadataOnly: event.target.checked })}
          className="sr-only"
        />
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
          <path d="M4 6h16M7 12h10M10 18h4" />
        </svg>
        File name
      </label>

      {activeCount > 0 && (
        <button
          type="button"
          onClick={() =>
            // Clears the narrowing filters but keeps the search MODE: mode is a
            // way of searching, not a restriction on results, and silently
            // flipping it back to semantic here would surprise.
            onChange({ metadataOnly: filters.metadataOnly })
          }
          className="text-[13px] font-medium text-ink-muted underline-offset-2 hover:text-ink hover:underline"
        >
          Clear filters ({activeCount})
        </button>
      )}
    </div>
  );
}

function DateField({
  label,
  value,
  onChange,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
}) {
  return (
    <label className="inline-flex items-center gap-1.5 text-[13px] text-ink-muted">
      {label}
      <input
        type="date"
        value={value}
        onChange={(event) => onChange(event.target.value)}
        aria-label={`Taken ${label.toLowerCase()} date`}
        className="rounded-md border border-line bg-surface px-2 py-1 text-[13px] text-ink focus:border-accent focus:outline-none [color-scheme:dark]"
      />
    </label>
  );
}

/**
 * A text input that commits on blur or Enter rather than per keystroke.
 *
 * It keeps a local draft while focused, then reports once. The effect re-syncs
 * the draft when the OUTSIDE value changes — the back button or "Clear filters"
 * can rewrite the URL under us, and without the sync the input would keep
 * showing stale text over a filter that no longer exists.
 */
