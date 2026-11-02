const UNIT = 34;
export const PLATE_DROP = 0.45;

export interface Point {
  x: number;
  y: number;
}

export interface Faces {
  top: string;
  left: string;
  right: string;
}

export interface Ground {
  x: number;
  y: number;
  w: number;
  d: number;
}

export function iso(x: number, y: number, z = 0): Point {
  return { x: (x - y) * UNIT * 0.866, y: (x + y) * UNIT * 0.5 - z * UNIT };
}

/** The turn between two ground points: first along one plate axis, then along the other. */
export function elbow(from: Point, to: Point): Point {
  const worldX = (p: Point) => (p.x / (UNIT * 0.866) + p.y / (UNIT * 0.5)) / 2;
  const worldY = (p: Point) => (p.y / (UNIT * 0.5) - p.x / (UNIT * 0.866)) / 2;
  return rounded(iso(worldX(to), worldY(from)));
}

/** Where a dot is at `t` of its hop, so it rides the drawn route and not a straight cut across it. */
export function pointOnRoute(from: Point, to: Point, t: number, turns = true): Point {
  if (t <= 0) return from;
  if (t >= 1) return to;
  if (!turns) return rounded({ x: from.x + (to.x - from.x) * t, y: from.y + (to.y - from.y) * t });
  const corner = elbow(from, to);
  const [start, end, share] = t < 0.5 ? [from, corner, t * 2] : [corner, to, t * 2 - 1];
  return rounded({ x: start.x + (end.x - start.x) * share, y: start.y + (end.y - start.y) * share });
}

export function rounded(point: Point): Point {
  return { x: Math.round(point.x * 10) / 10, y: Math.round(point.y * 10) / 10 };
}

export function points(list: [number, number, number][]): string {
  return list
    .map(([x, y, z]) => {
      const p = iso(x, y, z);
      return `${p.x.toFixed(1)},${p.y.toFixed(1)}`;
    })
    .join(" ");
}

export function box({ x, y, w, d }: Ground, z0: number, z1: number): Faces {
  return {
    top: points([[x, y, z1], [x + w, y, z1], [x + w, y + d, z1], [x, y + d, z1]]),
    left: points([[x, y + d, z1], [x + w, y + d, z1], [x + w, y + d, z0], [x, y + d, z0]]),
    right: points([[x + w, y, z1], [x + w, y + d, z1], [x + w, y + d, z0], [x + w, y, z0]]),
  };
}
