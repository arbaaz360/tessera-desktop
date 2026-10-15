import type { QueueName, QueueStatisticsDto } from '@immich/sdk';

/**
 * What the jobs page knows about each of Tessera's three queues.
 *
 * The names travel in Immich's shape (`/api/queues`, the SDK's `QueueName`
 * type) because that is the wire the page was written against; the queues
 * themselves are Tessera's, so the rules and words here are Tessera's too.
 *
 *   thumbnailGeneration  the grid thumbnail for every picture in a personal
 *                        account; corpus accounts get none by rule.
 *   library              folder scans. Not a queue: the watch scans small new
 *                        folders on its own (they are listed on this page),
 *                        a whole root is scanned from Admin. Pause stops the
 *                        watch from starting scans.
 *   music                names the song playing in each video, through the
 *                        identifier lanes; the worker count is how many
 *                        lanes are asked at once.
 *   faces                FaceSearch's queued ingest requests. Every scan of
 *                        a personal folder queues one; Run drains them one
 *                        at a time and switches itself off when done.
 *   embeddings           Foundation's resident GPU runner (SigLIP2, DINOv3,
 *                        observations) over everything registered and not
 *                        yet vectorised. Run is the switch; it turns itself
 *                        off when the pass ends.
 *
 * The last two are the GPU work: queued by scans, started by a person here.
 */
type Name = QueueName | string;

const THUMBNAILS = 'thumbnailGeneration';
const LIBRARY = 'library';
const MUSIC = 'music';
const FACES = 'faces';
const EMBEDDINGS = 'embeddings';

/** Queues with a "run" action. A scan is started per root, not from here. */
export const STARTABLE_QUEUES: ReadonlySet<Name> = new Set([THUMBNAILS, MUSIC, FACES, EMBEDDINGS]);

/** Queues whose run has no "force" variant. */
export const FORCELESS_QUEUES: ReadonlySet<Name> = new Set([FACES, EMBEDDINGS]);

export const UNPAUSABLE_QUEUES: ReadonlySet<Name> = new Set();

/** Queues where "parallel workers" means something Tessera controls. */
export const CONCURRENCY_QUEUES: ReadonlySet<Name> = new Set([THUMBNAILS, MUSIC]);

/** The GPU switches: their Run is "start draining", their Pause is "stop". */
export const SWITCH_QUEUES: ReadonlySet<Name> = new Set([FACES, EMBEDDINGS]);

const LABELS: Record<string, string> = {
  [THUMBNAILS]: 'Thumbnails',
  [LIBRARY]: 'Library scans',
  [MUSIC]: 'Music',
  [FACES]: 'Faces',
  [EMBEDDINGS]: 'Embeddings',
};

export function queueLabel(name: Name): string {
  const known = LABELS[name];
  if (known) return known;
  // "someCamelName" -> "Some Camel Name".
  const spaced = name.replaceAll(/([a-z])([A-Z])/g, '$1 $2');
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

const HINTS: Record<string, string> = {
  [THUMBNAILS]:
    'Grid thumbnails for personal accounts (corpus accounts get none). Run missing queues every picture without one; Force all redoes them all.',
  [LIBRARY]:
    'Small new folders are scanned by the watch and listed below; a whole root is scanned from Admin. Pause stops the watch starting scans.',
  [MUSIC]:
    'Names the song in each video through the identifier lanes. Run missing asks about videos never asked; Force all asks again. Workers = lanes asked at once.',
  [FACES]:
    'FaceSearch ingests (buffalo_l), one folder at a time on the GPU. Every scanned folder is queued here; Run works through the queue and switches itself off at the end.',
  [EMBEDDINGS]:
    "Foundation's SigLIP2 + DINOv3 pass over every library picture not yet vectorised (GPU). Run is the switch; it turns itself off when the pass ends. One GPU job at a time - refused while faces run.",
};

export function queueHint(name: Name): string | undefined {
  return HINTS[name];
}

/**
 * The number that has to reach zero before a run is done.
 *
 * `waiting` alone undercounts: jobs on a paused queue sit in `paused`, and
 * retrying jobs sit in `delayed`. All three are work not yet done, which is
 * the quantity someone watching a drain actually cares about.
 */
export function pendingCount(statistics: QueueStatisticsDto): number {
  return statistics.waiting + statistics.delayed + statistics.paused;
}
