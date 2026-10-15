import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react';
import type { TimelineAsset } from '@/shared/api/timeline';
import { rangeBetween } from './range';

/**
 * Multi-select, as client state.
 *
 * This is one of the few pieces of state in the app that is genuinely CLIENT
 * state — which assets the user has ticked exists nowhere on the server — so
 * it lives in React context rather than TanStack Query. The provider mounts
 * per PAGE, not at the app root: leaving the timeline should drop the
 * selection, and separate pages (Photos, Trash) must never share one.
 *
 * The shape is a `Set` of asset ids plus one anchor. A Set because membership
 * checks happen for every visible tile on every render, and `Set.has` is the
 * O(1) answer. The anchor remembers the last plain click so a later
 * shift-click can stretch a range from it.
 *
 * A limitation worth stating rather than hiding: shift-ranges only work
 * WITHIN one month bucket. Assets are fetched per bucket, so "everything
 * between March 3rd and June 9th" would require assets the client has never
 * downloaded. A shift-click whose anchor lives in another bucket falls back
 * to a plain toggle — see `rangeBetween`.
 */

export interface SelectionContextValue {
  /** Ids of the selected assets. Treat as immutable; a new Set per change. */
  selected: ReadonlySet<string>;
  count: number;
  /** True once anything is selected — tiles switch from "open" to "toggle". */
  active: boolean;
  isSelected: (id: string) => boolean;
  /** Plain click / checkbox click. Also moves the shift anchor here. */
  toggle: (asset: TimelineAsset, bucket: string) => void;
  /** Shift-click. `bucketAssets` is the clicked asset's bucket, in order. */
  selectRange: (asset: TimelineAsset, bucket: string, bucketAssets: readonly TimelineAsset[]) => void;
  clear: () => void;
}

const SelectionContext = createContext<SelectionContextValue | null>(null);

const EMPTY: ReadonlySet<string> = new Set();

export function SelectionProvider({ children }: { children: ReactNode }) {
  const [selected, setSelected] = useState<ReadonlySet<string>>(EMPTY);

  /*
   * The anchor lives in a ref, not state. It only matters at the moment of a
   * shift-click — nothing renders differently because of it — so promoting it
   * to state would re-render every consumer once per click for no visible
   * change.
   */
  const anchorRef = useRef<{ id: string; bucket: string } | null>(null);

  const toggle = useCallback((asset: TimelineAsset, bucket: string) => {
    anchorRef.current = { id: asset.id, bucket };
    setSelected((previous) => {
      const next = new Set(previous);
      if (next.has(asset.id)) {
        next.delete(asset.id);
      } else {
        next.add(asset.id);
      }
      return next;
    });
  }, []);

  const selectRange = useCallback(
    (asset: TimelineAsset, bucket: string, bucketAssets: readonly TimelineAsset[]) => {
      const anchor = anchorRef.current;
      const anchorId = anchor && anchor.bucket === bucket ? anchor.id : null;
      const ids = rangeBetween(anchorId, asset.id, bucketAssets);

      // No usable anchor (none set, or it is in another bucket): behave like
      // a plain click instead of doing something surprising.
      if (!ids) {
        toggle(asset, bucket);
        return;
      }

      // The anchor deliberately stays put, so successive shift-clicks re-pivot
      // around the same origin — the behaviour every file manager has taught
      // people's hands to expect.
      setSelected((previous) => {
        const next = new Set(previous);
        for (const id of ids) next.add(id);
        return next;
      });
    },
    [toggle],
  );

  const clear = useCallback(() => {
    anchorRef.current = null;
    setSelected(EMPTY);
  }, []);

  const isSelected = useCallback((id: string) => selected.has(id), [selected]);

  const value = useMemo<SelectionContextValue>(
    () => ({
      selected,
      count: selected.size,
      active: selected.size > 0,
      isSelected,
      toggle,
      selectRange,
      clear,
    }),
    [selected, isSelected, toggle, selectRange, clear],
  );

  return <SelectionContext.Provider value={value}>{children}</SelectionContext.Provider>;
}

/**
 * The strict hook, for components that only make sense inside a provider
 * (the selection bars). Throwing here turns a wiring mistake into an
 * immediate, named error instead of a silent no-op.
 */
export function useSelection(): SelectionContextValue {
  const context = useContext(SelectionContext);
  if (!context) {
    throw new Error('useSelection must be used inside a <SelectionProvider>');
  }
  return context;
}

/**
 * The optional hook, for components that WORK either way. `TimelineGrid` is
 * the customer: on pages that mount a provider it grows checkboxes, on pages
 * that do not (People, Albums today) it stays a plain grid. This is what lets
 * one grid serve both without forking the component.
 */
export function useSelectionOptional(): SelectionContextValue | null {
  return useContext(SelectionContext);
}
