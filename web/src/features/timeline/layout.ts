import type { TimelineAsset } from '@/shared/api/timeline';

/**
 * Justified row layout.
 *
 * Photos have wildly different aspect ratios. A fixed square grid crops them
 * all, which is fine for thumbnails and terrible for browsing — you lose the
 * shape of a panorama and the framing of a portrait. A justified layout instead
 * keeps every image's aspect ratio and varies row heights so each row fills the
 * container exactly. It is what Google Photos and Flickr do, for good reason.
 *
 * The algorithm is greedy and runs in O(n):
 *
 *   1. Add images to the current row, tracking the sum of their aspect ratios.
 *   2. Once that sum means the row would be shorter than the target height,
 *      close the row.
 *   3. Solve for the exact height that makes the row's total width equal the
 *      container width, gaps included.
 *
 * We can do all of this WITHOUT loading a single image, because the server
 * sends `ratio` per asset in the bucket payload. That is the difference between
 * a grid that renders instantly at the right size and one that reflows as
 * images trickle in.
 */

export interface LayoutTile {
  asset: TimelineAsset;
  /** Pixel offset from the left edge of the container. */
  x: number;
  width: number;
  height: number;
}

export interface LayoutRow {
  tiles: LayoutTile[];
  height: number;
  /** Pixel offset from the top of this bucket's block. */
  y: number;
}

export interface LayoutResult {
  rows: LayoutRow[];
  /** Total height of the block, so a virtualiser can size it without measuring. */
  totalHeight: number;
}

export interface LayoutOptions {
  containerWidth: number;
  /** Rows aim for roughly this height; actual height varies to justify. */
  targetRowHeight: number;
  gap: number;
}

/** Aspect ratios can arrive as 0 or NaN for a broken asset; keep them sane. */
function safeRatio(ratio: number): number {
  if (!Number.isFinite(ratio) || ratio <= 0) return 1;
  // Clamp extremes so one panorama cannot produce a 12-pixel-tall row.
  return Math.min(Math.max(ratio, 0.35), 4);
}

export function layoutAssets(assets: TimelineAsset[], options: LayoutOptions): LayoutResult {
  const { containerWidth, targetRowHeight, gap } = options;
  const rows: LayoutRow[] = [];

  if (containerWidth <= 0 || assets.length === 0) {
    return { rows, totalHeight: 0 };
  }

  let current: TimelineAsset[] = [];
  let ratioSum = 0;
  let y = 0;

  const closeRow = (isLastRow: boolean) => {
    if (current.length === 0) return;

    const totalGap = gap * (current.length - 1);
    const available = containerWidth - totalGap;

    // Height that makes this row exactly fill the container width.
    let height = available / ratioSum;

    // The final row is usually under-full. Stretching it to fill would blow up
    // a lone photo to full width, which looks like a bug rather than a
    // deliberate layout — so cap it at the target height and let it end short.
    if (isLastRow && height > targetRowHeight) {
      height = targetRowHeight;
    }

    let x = 0;
    const tiles: LayoutTile[] = current.map((asset) => {
      const width = safeRatio(asset.ratio) * height;
      const tile: LayoutTile = { asset, x, width, height };
      x += width + gap;
      return tile;
    });

    rows.push({ tiles, height, y });
    y += height + gap;

    current = [];
    ratioSum = 0;
  };

  for (const asset of assets) {
    current.push(asset);
    ratioSum += safeRatio(asset.ratio);

    // Would the row now be shorter than target? Then it is full.
    const totalGap = gap * (current.length - 1);
    const projectedHeight = (containerWidth - totalGap) / ratioSum;

    if (projectedHeight < targetRowHeight) {
      closeRow(false);
    }
  }

  closeRow(true);

  const totalHeight = rows.length === 0 ? 0 : y - gap;
  return { rows, totalHeight };
}

/**
 * Estimate a bucket's rendered height from its asset count alone.
 *
 * The virtualiser needs a height for every bucket up front, including ones
 * whose assets have not been fetched. Assuming an average ratio of ~1.4 (the
 * rough mean of phone photos and screenshots) gets close enough to keep the
 * scrollbar honest; the estimate is replaced by the real measurement as soon as
 * the bucket loads.
 */
export function estimateBucketHeight(count: number, options: LayoutOptions): number {
  const { containerWidth, targetRowHeight, gap } = options;
  if (containerWidth <= 0 || count === 0) return 0;

  const assumedRatio = 1.4;
  const perRow = Math.max(1, Math.round(containerWidth / (targetRowHeight * assumedRatio)));
  const rows = Math.ceil(count / perRow);

  return rows * targetRowHeight + Math.max(0, rows - 1) * gap;
}
