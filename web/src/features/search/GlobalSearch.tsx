import { useEffect, useState, type FormEvent } from 'react';
import { useNavigate, useParams, useRouterState, useSearch } from '@tanstack/react-router';
import { filtersToUrl, parseFilters } from './searchParams';

/**
 * The search box in the top bar.
 *
 * Submits rather than searching as you type. Semantic search runs a vector
 * query over 1.3M embeddings — issuing one per keystroke would hammer the
 * server for results nobody reads. Debouncing would be the usual answer, but
 * for a query this expensive, an explicit submit is both cheaper and clearer
 * about when work happens.
 *
 * The term lives in the URL, so a search is shareable, survives a refresh, and
 * works with the back button — none of which is true of a term kept in
 * component state.
 *
 * SCOPE FOLLOWS THE PAGE. Standing inside an album and typing a word means
 * "find this in what I am looking at", and the box used to answer with the
 * whole library — the one search where the result set is orders of magnitude
 * larger than the question. So the box inherits the album from the route and
 * pins it, exactly as the in-album box on the album page does. Everywhere else
 * the scope is the signed-in account, which is all the server would return
 * anyway: it filters every search by owner, and no partner sharing is
 * configured, so one account never sees another's photos.
 *
 * Two sources, in priority order:
 *   /albums/$albumId  — params.albumId, standing in the album itself
 *   /search?albumId=  — search.albumId, refining a scoped search from results
 *
 * `strict: false` is what makes both readable from a component that renders on
 * every route: the typed hooks require naming one route, and this box belongs
 * to none of them.
 */
export function GlobalSearch() {
  const navigate = useNavigate();
  const [term, setTerm] = useState('');

  const params = useParams({ strict: false }) as { albumId?: string };
  const search = useSearch({ strict: false }) as { albumId?: string } & Record<string, unknown>;
  const pathname = useRouterState({ select: (state) => state.location.pathname });
  const routeAlbumId = params.albumId ?? search.albumId;

  // The scope is inherited, not imposed: one click widens the next search back
  // to the whole account. Leaving the album clears the opt-out, so the choice
  // never outlives the context that produced it.
  const [widened, setWidened] = useState(false);
  useEffect(() => setWidened(false), [routeAlbumId]);

  const albumId = widened ? undefined : routeAlbumId;
  const scoped = albumId !== undefined;

  function onSubmit(event: FormEvent) {
    event.preventDefault();
    const query = term.trim();
    if (query.length > 1) {
      // On the search page the box refines the search on screen, so the mode
      // and the filters stay. Dropping them sent a file-name search back to
      // searching by meaning without a word (Windows UI test, 2026-10-16).
      const keep = pathname === '/search' ? filtersToUrl(parseFilters(search)) : {};
      void navigate({ to: '/search', search: { q: query, albumId, similar: undefined, ...keep } });
    }
  }

  return (
    <form onSubmit={onSubmit} className="max-w-md flex-1">
      <div className="relative">
        <span className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-ink-faint" aria-hidden>
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8">
            <circle cx="11" cy="11" r="7" />
            <path d="m20 20-3.5-3.5" />
          </svg>
        </span>

        <input
          type="search"
          value={term}
          onChange={(event) => setTerm(event.target.value)}
          placeholder={scoped ? 'Search in this album…' : "Search by what's in the photo…"}
          aria-label={scoped ? 'Search within this album' : 'Search photos'}
          className={`w-full rounded-md border border-line bg-ground py-1.5 pl-9 text-sm text-ink placeholder:text-ink-faint focus:border-accent focus:outline-none ${
            scoped ? 'pr-28' : 'pr-3'
          }`}
        />

        {/* The scope has to be VISIBLE. A box that silently searches a subset
            is indistinguishable from a library that has lost your photos. */}
        {scoped && (
          <button
            type="button"
            onClick={() => setWidened(true)}
            title="Search the whole library instead"
            className="absolute right-1.5 top-1/2 flex -translate-y-1/2 items-center gap-1 rounded border border-accent bg-accent-muted px-1.5 py-0.5 text-[11px] font-medium text-ink"
          >
            this album
            <span aria-hidden>×</span>
            <span className="sr-only">search the whole library instead</span>
          </button>
        )}
      </div>
    </form>
  );
}
