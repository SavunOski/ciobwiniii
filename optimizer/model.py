"""
model.py — Clinic Survival GAMSPy optimizer backend.

Each turn:
  1.  Compute congestion-aware, shortest-path travel costs from every house
      to every facility using Dijkstra on the road/grid network.
  2.  Use GAMSPy to solve an LP that assigns each house to its cheapest
      facility (min-cost assignment), producing the preferred_facility map.
  3.  Player road-building phase: BFS to connect clinic to all entities.
  4.  City (opposing) optimizer phase: spends citizen_budget to build roads
      that reduce average cost to hospitals.
  5.  Return full response to Godot.
"""

from collections import deque
import heapq

from gamspy import (
    Container, Equation, Model, Parameter,
    Problem, Sense, Set, Sum, Variable,
)

CARDINAL_STEPS = ((0, -1), (1, 0), (0, 1), (-1, 0))

# Road cost modifiers
BASE_ROAD_COST         = 1.0   # travel cost per road tile
OPEN_GROUND_COST       = 3.0   # travel cost per non-road tile
CONGESTION_PER_MALL    = 1.4   # added cost per adjacent mall
UPGRADE_DISCOUNT       = 1.5   # road cost reduction for upgraded roads
CLINIC_BASE_PENALTY    = 1.2   # clinic is slightly disadvantaged at start
HOSPITAL_FUNDING_CUT   = 4.0   # penalty added to all hospital routes when active


# ---------------------------------------------------------------------------
# Public entry point
# ---------------------------------------------------------------------------

def solve_turn(data: dict) -> dict:
    houses            = data["houses"]
    facilities        = data["facilities"]
    budget            = float(data["budget"])
    road_cost         = max(float(data.get("road_cost", 3.0)), 0.0001)
    setup_phase       = bool(data.get("setup_phase", False))
    citizen_budget    = float(data.get("citizen_budget", 0.0))
    hospital_cut      = bool(data.get("hospital_funding_cut", False))

    existing_roads    = {(int(p["x"]), int(p["y"])) for p in data.get("existing_roads", [])}
    mall_positions    = {(int(p["x"]), int(p["y"])) for p in data.get("mall_positions", [])}
    upgraded_roads    = {(int(p["x"]), int(p["y"])) for p in data.get("upgraded_roads", [])}
    blocked_roads     = {(int(p["x"]), int(p["y"])) for p in data.get("blocked_roads", [])}

    occupied          = {(int(e["x"]), int(e["y"])) for e in [*houses, *facilities]}
    grid_size         = 20

    house_ids         = [h["id"] for h in houses]
    facility_ids      = [f["id"] for f in facilities]

    # -----------------------------------------------------------------------
    # 1.  Compute shortest-path travel costs
    # -----------------------------------------------------------------------
    # Build edge-weight grid: cost to *enter* each tile
    def tile_cost(pos):
        if pos in blocked_roads:
            return 99999.0                   # impassable
        if pos in upgraded_roads:
            return BASE_ROAD_COST - UPGRADE_DISCOUNT + 0.5   # cheaper
        if pos in existing_roads:
            base = BASE_ROAD_COST
        else:
            base = OPEN_GROUND_COST
        # mall congestion
        congestion = sum(
            CONGESTION_PER_MALL
            for mp in mall_positions
            if abs(mp[0] - pos[0]) + abs(mp[1] - pos[1]) <= 1
        )
        return base + congestion

    def dijkstra_from(start_pos):
        """Return dict of {tile: min_cost} from start_pos."""
        dist = {}
        heap = [(0.0, start_pos)]
        while heap:
            cost, pos = heapq.heappop(heap)
            if pos in dist:
                continue
            dist[pos] = cost
            x, y = pos
            for dx, dy in CARDINAL_STEPS:
                nxt = (x + dx, y + dy)
                if nxt[0] < 0 or nxt[1] < 0 or nxt[0] >= grid_size or nxt[1] >= grid_size:
                    continue
                nc = cost + tile_cost(nxt)
                if nxt not in dist:
                    heapq.heappush(heap, (nc, nxt))
        return dist

    # Map: facility_id → position
    facility_pos = {f["id"]: (f["x"], f["y"]) for f in facilities}
    house_pos    = {h["id"]: (h["x"], h["y"]) for h in houses}

    # Per-facility Dijkstra (source at facility, cost to reach each house)
    facility_dist = {}
    for fid in facility_ids:
        facility_dist[fid] = dijkstra_from(facility_pos[fid])

    # Effective travel cost house → facility
    travel_costs = {}
    for hid in house_ids:
        hpos = house_pos[hid]
        for fid in facility_ids:
            base_c = facility_dist[fid].get(hpos, 99999.0)
            # Clinic penalty (slight structural disadvantage)
            if fid == "Clinic":
                base_c += CLINIC_BASE_PENALTY
            # Hospital funding cut
            if hospital_cut and fid != "Clinic":
                base_c += HOSPITAL_FUNDING_CUT
            travel_costs[(hid, fid)] = max(0.01, base_c)

    # -----------------------------------------------------------------------
    # 2.  GAMSPy LP: minimise total travel cost assignment
    #     Each house is assigned to exactly one facility (min-cost).
    #     The solver naturally picks the cheapest facility per house.
    # -----------------------------------------------------------------------
    m = Container()

    h_set = Set(m, name="h", records=house_ids)
    f_set = Set(m, name="f", records=facility_ids)

    cost_records = [
        (hid, fid, travel_costs[(hid, fid)])
        for hid in house_ids
        for fid in facility_ids
    ]
    c = Parameter(m, name="c", domain=[h_set, f_set], records=cost_records)

    # x[h,f] ∈ [0,1] — assignment fraction (LP relaxation; optimal solution is binary)
    x = Variable(m, name="x", domain=[h_set, f_set], type="positive")

    # Each house fully assigned to exactly one facility
    assign_eq = Equation(m, name="assign_eq", domain=h_set)
    assign_eq[h_set] = Sum(f_set, x[h_set, f_set]) == 1

    # x ≤ 1
    cap_eq = Equation(m, name="cap_eq", domain=[h_set, f_set])
    cap_eq[h_set, f_set] = x[h_set, f_set] <= 1

    obj_var = Variable(m, name="obj_var")
    obj_eq  = Equation(m, name="obj_eq")
    obj_eq[...] = obj_var == Sum([h_set, f_set], c[h_set, f_set] * x[h_set, f_set])

    lp_model = Model(
        m,
        name="clinic_survival",
        equations=[assign_eq, cap_eq, obj_eq],
        problem=Problem.LP,
        sense=Sense.MIN,
        objective=obj_var,
    )
    lp_model.solve()

    # Extract preferred facility per house (min-cost assignment)
    preferred_facility: dict[str, str] = {}
    for hid in house_ids:
        best_fid = min(facility_ids, key=lambda fid: travel_costs[(hid, fid)])
        preferred_facility[hid] = best_fid

    # If GAMSPy solved OK, use its x values (should match argmin)
    try:
        x_vals = x.records
        if x_vals is not None and not x_vals.empty:
            for hid in house_ids:
                rows = x_vals[x_vals.iloc[:, 0] == hid]
                if not rows.empty:
                    best_row = rows.loc[rows.iloc[:, 2].idxmax()]
                    preferred_facility[hid] = best_row.iloc[1]
    except Exception:
        pass  # fallback to argmin already computed above

    clinic_count = sum(1 for v in preferred_facility.values() if v == "Clinic")
    clinic_ratio = clinic_count / max(1, len(house_ids))

    # -----------------------------------------------------------------------
    # 3.  Player road-building phase (clinic → all entities)
    # -----------------------------------------------------------------------
    built_roads = []
    if not setup_phase and budget >= road_cost:
        built_roads = build_road_network(
            houses=houses,
            facilities=facilities,
            existing_roads=existing_roads,
            budget=budget,
            road_cost=road_cost,
            setup_phase=False,
            favor_clinic=True,
        )
    elif setup_phase:
        built_roads = build_road_network(
            houses=houses,
            facilities=facilities,
            existing_roads=existing_roads,
            budget=budget,
            road_cost=road_cost,
            setup_phase=True,
            favor_clinic=True,
        )

    spent_budget = 0.0 if setup_phase else len(built_roads) * road_cost

    # -----------------------------------------------------------------------
    # 4.  City (opposing) optimizer: spends citizen_budget on hospital roads
    # -----------------------------------------------------------------------
    optimizer_roads = []
    if citizen_budget >= road_cost and not setup_phase:
        all_roads_so_far = existing_roads | set(map(tuple, built_roads))
        optimizer_roads = build_road_network(
            houses=houses,
            facilities=[f for f in facilities if f["id"] != "Clinic"],
            existing_roads=all_roads_so_far,
            budget=citizen_budget,
            road_cost=road_cost,
            setup_phase=False,
            favor_clinic=False,
        )

    return {
        "preferred_facility": preferred_facility,
        "clinic_count": clinic_count,
        "clinic_ratio": clinic_ratio,
        "clinic_alive": clinic_ratio >= 0.35,
        "citizen_cost_objective": float(lp_model.objective_value or 0.0),
        "solver_status": str(lp_model.solve_status),
        "budget_seen": budget,
        "spent_budget": spent_budget,
        "built_roads": [{"x": x, "y": y} for x, y in built_roads],
        "optimizer_roads": [{"x": x, "y": y} for x, y in optimizer_roads],
    }


# ---------------------------------------------------------------------------
# Road-building BFS helpers
# ---------------------------------------------------------------------------

def build_road_network(
    houses: list[dict],
    facilities: list[dict],
    existing_roads: set[tuple[int, int]],
    budget: float,
    road_cost: float,
    setup_phase: bool,
    favor_clinic: bool = True,
) -> list[tuple[int, int]]:
    all_entities = [*houses, *facilities]
    occupied = {(int(e["x"]), int(e["y"])) for e in all_entities}

    if not facilities:
        return []

    # Seed road network from primary facility
    primary = facilities[0]
    primary_pos = (primary["x"], primary["y"])
    connectors = get_connectors(primary_pos, occupied, 22, 22)
    if not connectors:
        return []

    road_network = set(existing_roads)
    built_roads: list[tuple[int, int]] = []

    if not road_network:
        seed = connectors[0]
        road_network.add(seed)
        built_roads.append(seed)

    targets = [e for e in all_entities if (e["x"], e["y"]) != primary_pos]
    remaining_tiles = None if setup_phase else max(0, int(budget // road_cost))

    pending = list(targets)
    while pending:
        options = []
        for entity in pending:
            ep = (entity["x"], entity["y"])
            cons = get_connectors(ep, occupied, 22, 22)
            path = shortest_connector_path(cons, road_network, occupied, 22, 22)
            if path is None:
                continue
            missing = [t for t in path if t not in road_network]
            options.append((len(missing), entity["id"], entity, missing))

        if not options:
            break

        options.sort(key=lambda item: (item[0], item[1]))

        chosen = None
        for option in options:
            if remaining_tiles is None or len(option[3]) <= remaining_tiles:
                chosen = option
                break

        if chosen is None:
            break

        _, _, entity, missing_tiles = chosen
        pending.remove(entity)

        for tile in missing_tiles:
            if tile in road_network:
                continue
            road_network.add(tile)
            built_roads.append(tile)

        if remaining_tiles is not None:
            remaining_tiles -= len(missing_tiles)

    return built_roads


def get_connectors(
    origin: tuple[int, int],
    occupied: set[tuple[int, int]],
    max_x: int,
    max_y: int,
) -> list[tuple[int, int]]:
    ox, oy = origin
    return [
        (ox + dx, oy + dy)
        for dx, dy in CARDINAL_STEPS
        if 0 <= ox + dx <= max_x
        and 0 <= oy + dy <= max_y
        and (ox + dx, oy + dy) not in occupied
    ]


def shortest_connector_path(
    connectors: list[tuple[int, int]],
    road_network: set[tuple[int, int]],
    occupied: set[tuple[int, int]],
    max_x: int,
    max_y: int,
) -> list[tuple[int, int]] | None:
    best = None
    for connector in connectors:
        path = shortest_path_to_network(connector, road_network, occupied, max_x, max_y)
        if path is None:
            continue
        if best is None or len(path) < len(best):
            best = path
    return best


def shortest_path_to_network(
    start: tuple[int, int],
    road_network: set[tuple[int, int]],
    occupied: set[tuple[int, int]],
    max_x: int,
    max_y: int,
) -> list[tuple[int, int]] | None:
    if start in road_network:
        return [start]

    visited = {start}
    queue = deque([(start, [start])])

    while queue:
        point, path = queue.popleft()
        for dx, dy in CARDINAL_STEPS:
            nxt = (point[0] + dx, point[1] + dy)
            if nxt[0] < 0 or nxt[1] < 0 or nxt[0] > max_x or nxt[1] > max_y:
                continue
            if nxt in visited:
                continue
            if nxt in occupied and nxt not in road_network:
                continue

            next_path = path + [nxt]
            if nxt in road_network:
                return next_path

            visited.add(nxt)
            queue.append((nxt, next_path))

    return None
