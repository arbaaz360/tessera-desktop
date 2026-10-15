import type { TimelineAssetType } from '@/shared/api/timeline';

export type MediaTypeValue = TimelineAssetType | 'ALL';

const OPTIONS: { value: MediaTypeValue; label: string }[] = [
  { value: 'ALL', label: 'All' },
  { value: 'IMAGE', label: 'Photos' },
  { value: 'VIDEO', label: 'Videos' },
];

/**
 * Segmented control for the media-type filter.
 *
 * Radio inputs rather than buttons, so keyboard users get arrow-key navigation
 * and screen readers announce it as a single grouped choice. The visual
 * treatment is entirely CSS on top of native semantics — which is almost always
 * cheaper than rebuilding the accessibility of a native control from scratch.
 */
export function MediaTypeFilter({
  value,
  onChange,
}: {
  value: MediaTypeValue;
  onChange: (value: MediaTypeValue) => void;
}) {
  return (
    <fieldset
      className="inline-flex items-center rounded-lg border border-line bg-surface p-0.5"
      aria-label="Filter by media type"
    >
      {OPTIONS.map((option) => {
        const checked = option.value === value;
        return (
          <label
            key={option.value}
            className={`cursor-pointer rounded-md px-3 py-1.5 text-[13px] font-medium transition-colors ${
              checked ? 'bg-accent text-ink-inverse' : 'text-ink-muted hover:text-ink'
            }`}
          >
            <input
              type="radio"
              name="media-type"
              value={option.value}
              checked={checked}
              onChange={() => onChange(option.value)}
              className="sr-only"
            />
            {option.label}
          </label>
        );
      })}
    </fieldset>
  );
}
