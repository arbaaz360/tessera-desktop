import { useState, type ReactNode } from 'react';
import { readThemeChoice, writeThemeChoice, type ThemeChoice } from './theme';

/**
 * Three-way theme control: dark, light, or follow the OS.
 *
 * Three options rather than a two-state switch, because "follow the OS" is a
 * genuinely different intent from either fixed choice — and it was the previous
 * behaviour, so removing it outright would take something away from anyone who
 * liked it. It is simply no longer the default.
 *
 * Same construction as the app's other segmented controls: native radios under
 * CSS, so arrow-key navigation and group announcement come from the platform
 * rather than being reimplemented.
 */
const OPTIONS: { value: ThemeChoice; label: string; icon: ReactNode }[] = [
  {
    value: 'dark',
    label: 'Dark',
    icon: (
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
        <path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8Z" />
      </svg>
    ),
  },
  {
    value: 'light',
    label: 'Light',
    icon: (
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
        <circle cx="12" cy="12" r="4" />
        <path d="M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M19.1 4.9l-1.4 1.4M6.3 17.7l-1.4 1.4" />
      </svg>
    ),
  },
  {
    value: 'system',
    label: 'System',
    icon: (
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden>
        <rect x="2" y="4" width="20" height="13" rx="2" />
        <path d="M8 21h8M12 17v4" />
      </svg>
    ),
  },
];

export function ThemeToggle() {
  // Initialised from storage rather than defaulted, so the control agrees with
  // what `initTheme()` already put on <html> before React mounted.
  const [choice, setChoice] = useState<ThemeChoice>(() => readThemeChoice());

  const select = (next: ThemeChoice) => {
    setChoice(next);
    writeThemeChoice(next);
  };

  return (
    <fieldset
      className="inline-flex items-center rounded-lg border border-line bg-surface p-0.5"
      aria-label="Colour theme"
    >
      {OPTIONS.map((option) => {
        const checked = option.value === choice;
        return (
          <label
            key={option.value}
            title={option.label === 'System' ? 'Follow the operating system' : option.label}
            className={`cursor-pointer rounded-md px-2 py-1.5 transition-colors ${
              checked ? 'bg-accent text-ink-inverse' : 'text-ink-muted hover:text-ink'
            }`}
          >
            <input
              type="radio"
              name="theme"
              value={option.value}
              checked={checked}
              onChange={() => select(option.value)}
              className="sr-only"
              aria-label={option.label}
            />
            {option.icon}
          </label>
        );
      })}
    </fieldset>
  );
}
