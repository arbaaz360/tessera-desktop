import { describe, expect, it } from 'vitest';
import {
  CONCURRENCY_QUEUES,
  FORCELESS_QUEUES,
  STARTABLE_QUEUES,
  SWITCH_QUEUES,
  UNPAUSABLE_QUEUES,
  pendingCount,
  queueHint,
  queueLabel,
} from './queues';

const TESSERA_QUEUES = ['thumbnailGeneration', 'library', 'music', 'faces', 'embeddings'];

describe('queueLabel', () => {
  it('names each of the five queues in plain words', () => {
    expect(queueLabel('thumbnailGeneration')).toBe('Thumbnails');
    expect(queueLabel('library')).toBe('Library scans');
    expect(queueLabel('music')).toBe('Music');
    expect(queueLabel('faces')).toBe('Faces');
    expect(queueLabel('embeddings')).toBe('Embeddings');
  });

  it('still produces something readable for a name it has never seen', () => {
    // A server that grows a queue must not render a blank card.
    expect(queueLabel('someNewQueue')).toBe('Some New Queue');
  });

  it('has a hint for every queue the server knows', () => {
    for (const name of TESSERA_QUEUES) {
      expect(queueHint(name)?.length ?? 0).toBeGreaterThan(20);
    }
  });
});

describe('pendingCount', () => {
  const base = { active: 3, completed: 100, failed: 2, delayed: 0, waiting: 0, paused: 0 };

  it('sums waiting, delayed and paused', () => {
    expect(pendingCount({ ...base, waiting: 10, delayed: 5, paused: 1 })).toBe(16);
  });

  it('excludes active, completed and failed — none of them are still owed', () => {
    expect(pendingCount(base)).toBe(0);
  });
});

describe('queue capability sets', () => {
  it('never offers force on a queue that cannot start at all', () => {
    for (const name of FORCELESS_QUEUES) {
      expect(STARTABLE_QUEUES.has(name)).toBe(true);
    }
  });

  it('a scan is started per root, never from the card', () => {
    expect(STARTABLE_QUEUES.has('library')).toBe(false);
  });

  it('the GPU switches have no force and no worker count', () => {
    for (const name of SWITCH_QUEUES) {
      expect(FORCELESS_QUEUES.has(name)).toBe(true);
      expect(CONCURRENCY_QUEUES.has(name)).toBe(false);
    }
  });

  it('every queue can be paused', () => {
    expect([...UNPAUSABLE_QUEUES]).toEqual([]);
  });
});
