"""
model.py — Clinic Survival GAMSPy optimizer backend.

Key design rules:
  - Citizens can ONLY travel on road tiles and tiles directly adjacent to
    buildings (houses / hospitals / clinic).  Open grass is IMPASSABLE.
  - If a house has no road-connected path to any facility, it is DISCONNECTED
    and receives no healthcare.
  - The city optimizer (LP #2) maximises healthcare ACCESS — it builds the
    fewest roads needed to connect disconnected houses to hospitals.
  - Clinic congestion: roads near the clinic become more expensive as more
    patients use it.
"""

from collections import deque
import heapq

from gamspy import (
    Container, Equation, Model, Parameter,
    Problem, Sense, Set, Sum, Variable,
)

CARDINAL_STEPS = ((0, -1), (1, 0), (0, 1), (-1, 0))

# Travel cost constants
BASE_ROAD_COST       = 1.0
CONGESTION_PER_MALL  = 1.8
UPGRADE_DISCOUNT     = 0.6
CLINIC_BASE_PENALTY  = 1.5
HOSPITAL_FUNDING_CUT = 4.0
CLINIC_CONGESTION_PER_PATIENT = 0.6


def solve_turn(data: dict) -> dict:
    houses         = data["houses"]
    facilities     = data["facilities"]
    budget         = float(data["budget"])
    road_cost      = max(float(data.get("road_cost", 3.0)), 0.0001)
    setup_phase    = bool(data.get("setup_phase", False))
    citizen_budget = float(data.get("citizen_budget", 0.0))
    hospital_cut   = bool(data.get("hospital_funding_cut", False))

    existing_roads = {(int(p["x"]), int(p["y"])) for p in data.get("existing_roads", [])}
    mall_positions = {(int(p["x"]), int(p["y"])) for p in data.get("mall_positions", [])}
    upgraded_roads = {(int(p["x"]), int(p["y"])) for p in data.get("upgraded_roads", [])}
    blocked_roads  = {(int(p["x"]), int(p["y"])) for p in data.get("blocked_roads", [])}
    turn_index     = int(data.get("turn_index", 0))

    occupied   = {(int(e["x"]), int(e["y"])) for e in [*houses, *facilities]}
    grid_size  = 20
    house_ids  = [h["id"] for h in houses]
    facility_ids = [f["id"] for f in facilities]

    clinic = next((f for f in facilities if f["id"] == "Clinic"), None)
    clinic_pos = (clinic["x"], clinic["y"]) if clinic else None
    hospitals = [f for f in facilities if f["id"] != "Clinic"]

    facility_pos  = {f["id"]: (f["x"], f["y"]) for f in facilities}
    house_pos_map = {h["id"]: (h["x"], h["y"]) for h in houses}

    # -----------------------------------------------------------------------
    # Build the set of WALKABLE tiles:
    #   - All road tiles (that aren't blocked)
    #   - Building tiles themselves (endpoints, always walkable)
    #   - Connector tiles (tiles directly adjacent to a building) ONLY when
    #     they are also adjacent to at least one actual road tile.
    #     This prevents isolated buildings (no roads) from being reachable
    #     through pure connector-zone adjacency chains across empty grass.
    # -----------------------------------------------------------------------
    building_connectors: set[tuple[int, int]] = set()
    for pos in occupied:
        building_connectors.add(pos)
        ox, oy = pos
        for dx, dy in CARDINAL_STEPS:
            nxt = (ox + dx, oy + dy)
            if 0 <= nxt[0] < grid_size and 0 <= nxt[1] < grid_size:
                building_connectors.add(nxt)

    # Step 1: start walkable from actual road tiles + building tiles (endpoints)
    road_tiles: set[tuple[int, int]] = {
        r for r in existing_roads if r not in blocked_roads
    }
    walkable: set[tuple[int, int]] = road_tiles | occupied

    # Step 2: admit connector tiles only when they touch a real road
    for tile in building_connectors:
        if tile in walkable:
            continue  # already included
        tx, ty = tile
        for dx, dy in CARDINAL_STEPS:
            adj = (tx + dx, ty + dy)
            if adj in road_tiles:
                walkable.add(tile)
                break

    def make_cost_fn(clinic_patient_count=0):
        clinic_adj = set()
        if clinic_pos and clinic_patient_count > 0:
            cx, cy = clinic_pos
            for dx in range(-2, 3):
                for dy in range(-2, 3):
                    if abs(dx) + abs(dy) <= 2:
                        clinic_adj.add((cx + dx, cy + dy))

        def tile_cost(pos):
            if pos not in walkable:
                return None  # impassable
            if pos in blocked_roads:
                return None  # blocked
            if pos in upgraded_roads:
                base = max(0.2, BASE_ROAD_COST - UPGRADE_DISCOUNT)
            elif pos in existing_roads:
                base = BASE_ROAD_COST
            elif pos in building_connectors:
                base = BASE_ROAD_COST * 1.2  # connectors slightly more expensive
            else:
                return None  # shouldn't happen, but safety

            # Mall congestion
            cong = sum(
                CONGESTION_PER_MALL
                for mp in mall_positions
                if abs(mp[0] - pos[0]) + abs(mp[1] - pos[1]) <= 1
            )
            # Clinic congestion
            clinic_cong = (
                clinic_patient_count * CLINIC_CONGESTION_PER_PATIENT
                if pos in clinic_adj else 0.0
            )
            return base + cong + clinic_cong

        return tile_cost

    def dijkstra_from(start_pos, cost_fn):
        """Return {tile: min_cost} dict.  Only walkable tiles appear."""
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
                tc = cost_fn(nxt)
                if tc is None:
                    continue  # impassable
                nc = cost + tc
                if nxt not in dist:
                    heapq.heappush(heap, (nc, nxt))
        return dist

    UNREACHABLE = 999999.0

    def compute_travel_costs(cost_fn):
        fac_dist = {fid: dijkstra_from(facility_pos[fid], cost_fn) for fid in facility_ids}
        tc = {}
        for hid in house_ids:
            hpos = house_pos_map[hid]
            for fid in facility_ids:
                base_c = fac_dist[fid].get(hpos, UNREACHABLE)
                if base_c >= UNREACHABLE:
                    tc[(hid, fid)] = UNREACHABLE
                    continue
                if fid == "Clinic":
                    base_c += CLINIC_BASE_PENALTY
                if hospital_cut and fid != "Clinic":
                    base_c += HOSPITAL_FUNDING_CUT
                tc[(hid, fid)] = max(0.01, base_c)
        return tc

    # --- Pass 1: no clinic congestion ---
    cost_fn_p1 = make_cost_fn(clinic_patient_count=0)
    tc_p1 = compute_travel_costs(cost_fn_p1)

    preferred_p1 = {}
    for hid in house_ids:
        best_fid = min(facility_ids, key=lambda fid: tc_p1[(hid, fid)])
        if tc_p1[(hid, best_fid)] >= UNREACHABLE:
            preferred_p1[hid] = None  # disconnected
        else:
            preferred_p1[hid] = best_fid

    clinic_count_p1 = sum(1 for v in preferred_p1.values() if v == "Clinic")

    # --- Pass 2: with clinic congestion ---
    cost_fn_p2 = make_cost_fn(clinic_patient_count=clinic_count_p1)
    travel_costs = compute_travel_costs(cost_fn_p2)

    # -----------------------------------------------------------------------
    # GAMSPy LP #1 — Min-cost facility assignment
    #
    # Only houses that can actually REACH a facility are included.
    # Disconnected houses get preferred_facility = None.
    # -----------------------------------------------------------------------
    connected_houses = []
    disconnected_houses = []
    for hid in house_ids:
        best_cost = min(travel_costs[(hid, fid)] for fid in facility_ids)
        if best_cost >= UNREACHABLE:
            disconnected_houses.append(hid)
        else:
            connected_houses.append(hid)

    preferred_facility: dict[str, str | None] = {}
    for hid in disconnected_houses:
        preferred_facility[hid] = None

    solver_status = "NoConnectedHouses"
    objective_value = 0.0

    if connected_houses:
        m = Container()

        h_set = Set(m, name="h", records=connected_houses)
        f_set = Set(m, name="f", records=facility_ids)

        cost_records = [
            (hid, fid, travel_costs[(hid, fid)])
            for hid in connected_houses
            for fid in facility_ids
        ]
        c = Parameter(m, name="c", domain=[h_set, f_set], records=cost_records)

        x = Variable(m, name="x", domain=[h_set, f_set], type="positive")

        assign_eq = Equation(m, name="assign", domain=h_set)
        assign_eq[h_set] = Sum(f_set, x[h_set, f_set]) == 1

        cap_eq = Equation(m, name="cap", domain=[h_set, f_set])
        cap_eq[h_set, f_set] = x[h_set, f_set] <= 1

        obj_var = Variable(m, name="obj_var")
        obj_eq = Equation(m, name="obj_eq")
        obj_eq[...] = obj_var == Sum([h_set, f_set], c[h_set, f_set] * x[h_set, f_set])

        lp1 = Model(
            m, name="assignment",
            equations=[assign_eq, cap_eq, obj_eq],
            problem=Problem.LP, sense=Sense.MIN, objective=obj_var,
        )
        lp1.solve()
        solver_status = str(lp1.solve_status)
        objective_value = float(lp1.objective_value or 0.0)

        # Extract assignments
        for hid in connected_houses:
            preferred_facility[hid] = min(
                facility_ids, key=lambda fid: travel_costs[(hid, fid)]
            )

        try:
            x_vals = x.records
            if x_vals is not None and not x_vals.empty:
                for hid in connected_houses:
                    rows = x_vals[x_vals.iloc[:, 0] == hid]
                    if not rows.empty:
                        best_row = rows.loc[rows.iloc[:, 2].idxmax()]
                        assigned_fid = best_row.iloc[1]
                        if travel_costs[(hid, assigned_fid)] < UNREACHABLE:
                            preferred_facility[hid] = assigned_fid
        except Exception:
            pass

    # Count clinic patients (only connected houses)
    clinic_count = sum(1 for v in preferred_facility.values() if v == "Clinic")
    clinic_ratio = clinic_count / max(1, len(house_ids))

    # -----------------------------------------------------------------------
    # Setup phase — BFS road network seeding
    # -----------------------------------------------------------------------
    built_roads = []
    if setup_phase:
        built_roads = build_road_network(
            houses=houses, facilities=facilities,
            existing_roads=existing_roads,
            budget=budget, road_cost=road_cost, setup_phase=True,
        )

    # -----------------------------------------------------------------------
    # City optimizer (LP #2) — Maximise healthcare access
    #
    # The city builds roads to:
    #   a) Connect disconnected houses to ANY facility (primary goal)
    #   b) Among clinic-routed houses, redirect to hospitals (secondary)
    #
    # Formulation:
    #   Candidate tiles = tiles on shortest-path-through-new-roads from
    #   each target house to its nearest hospital.
    #   max  Σ_h  w[h] * z[h]
    #   s.t. Σ_r  y[r]  ≤  max_tiles        (budget)
    #        z[h] ≤  (1/need[h]) * Σ_r in path[h] y[r]
    #        0 ≤ y[r] ≤ 1,  0 ≤ z[h] ≤ 1
    #
    # Weights: disconnected houses get w=10, clinic-routed get w=2.
    # need[h] = number of new tiles needed on the path for house h.
    # -----------------------------------------------------------------------
    optimizer_roads = []
    if not setup_phase and citizen_budget >= road_cost and hospitals:
        all_roads_now = existing_roads | {tuple(r) for r in built_roads}
        optimizer_roads = city_optimizer(
            houses=houses,
            house_pos_map=house_pos_map,
            hospitals=hospitals,
            clinic_pos=clinic_pos,
            preferred_facility=preferred_facility,
            all_roads=all_roads_now,
            occupied=occupied,
            blocked_roads=blocked_roads,
            citizen_budget=citizen_budget,
            road_cost=road_cost,
            grid_size=grid_size,
        )

    # -----------------------------------------------------------------------
    # After turn 5: if hospitals have 0 patients, city destroys clinic roads
    #
    # Strategy: find road tiles most critical to clinic routing
    # (appear on the most house→clinic shortest paths), and remove them.
    # Cost is deducted from citizen_budget proportionally.
    # -----------------------------------------------------------------------
    destroyed_roads = []
    hospital_patients = sum(
        1 for v in preferred_facility.values()
        if v is not None and v != "Clinic"
    )
    destroy_budget = citizen_budget * 0.4  # city can spend up to 40% of budget on destruction

    if not setup_phase and turn_index >= 5 and hospital_patients == 0 and clinic_pos:
        destroyed_roads = find_clinic_roads_to_destroy(
            houses=houses,
            house_pos_map=house_pos_map,
            preferred_facility=preferred_facility,
            clinic_pos=clinic_pos,
            all_roads=existing_roads | {tuple(r) for r in optimizer_roads},
            occupied=occupied,
            cost_fn=cost_fn_p2,
            grid_size=grid_size,
            max_destroy=max(1, int(destroy_budget // road_cost)),
        )

    # Build the response — None preferred facilities become "Disconnected"
    pf_response = {}
    for hid, fid in preferred_facility.items():
        pf_response[hid] = fid if fid is not None else "Disconnected"

    return {
        "preferred_facility": pf_response,
        "clinic_count": clinic_count,
        "clinic_ratio": clinic_ratio,
        "clinic_alive": True,
        "citizen_cost_objective": objective_value,
        "solver_status": solver_status,
        "budget_seen": budget,
        "spent_budget": 0.0,
        "built_roads": [{"x": x, "y": y} for x, y in built_roads],
        "optimizer_roads": [{"x": rx, "y": ry} for rx, ry in optimizer_roads],
        "destroyed_roads": [{"x": rx, "y": ry} for rx, ry in destroyed_roads],
    }


# ---------------------------------------------------------------------------
# Road destruction helper — finds clinic-routing critical tiles
# ---------------------------------------------------------------------------

def find_clinic_roads_to_destroy(
    houses, house_pos_map, preferred_facility, clinic_pos,
    all_roads, occupied, cost_fn, grid_size, max_destroy,
) -> list[tuple[int, int]]:
    """
    Find road tiles that are most critical to clinic routing.

    For each house currently routed to the clinic, run Dijkstra and reconstruct
    the path.  Count how many clinic-bound paths each road tile appears on.
    Return the top max_destroy tiles by frequency — removing these will
    disrupt the most clinic routes simultaneously.
    """
    if max_destroy <= 0:
        return []

    # Dijkstra with path reconstruction from house → clinic
    def dijkstra_path_to(start, goal):
        if start == goal:
            return []
        dist = {start: 0.0}
        prev = {}
        heap = [(0.0, start)]
        while heap:
            cost, pos = heapq.heappop(heap)
            if pos == goal:
                path = []
                cur = goal
                while cur != start:
                    path.append(cur)
                    cur = prev.get(cur)
                    if cur is None:
                        break
                return path  # reversed, but we only care about tiles in it
            if cost > dist.get(pos, float("inf")):
                continue
            x, y = pos
            for dx, dy in CARDINAL_STEPS:
                nxt = (x + dx, y + dy)
                if nxt[0] < 0 or nxt[1] < 0 or nxt[0] >= grid_size or nxt[1] >= grid_size:
                    continue
                tc = cost_fn(nxt)
                if tc is None:
                    continue
                nc = cost + tc
                if nc < dist.get(nxt, float("inf")):
                    dist[nxt] = nc
                    prev[nxt] = pos
                    heapq.heappush(heap, (nc, nxt))
        return None

    road_usage: dict[tuple[int, int], int] = {}

    for house in houses:
        hid = house["id"]
        if preferred_facility.get(hid) != "Clinic":
            continue
        hpos = house_pos_map[hid]
        path = dijkstra_path_to(hpos, clinic_pos)
        if path is None:
            continue
        for tile in path:
            if tile in all_roads and tile not in occupied:
                road_usage[tile] = road_usage.get(tile, 0) + 1

    if not road_usage:
        return []

    # Sort by usage frequency, pick the top tiles
    ranked = sorted(road_usage.keys(), key=lambda t: -road_usage[t])
    return ranked[:max_destroy]


# City optimizer — GAMSPy LP #2
# ---------------------------------------------------------------------------

def city_optimizer(
    houses, house_pos_map, hospitals, clinic_pos,
    preferred_facility, all_roads, occupied,
    blocked_roads, citizen_budget, road_cost, grid_size,
) -> list[tuple[int, int]]:
    """
    Build roads to maximise healthcare access.

    Priority:
      1. Disconnected houses (weight 10) — connect them to nearest hospital
      2. Clinic-preferring houses (weight 2) — redirect to nearest hospital

    Uses BFS on the full grid to find shortest paths from target houses to
    hospitals, then solves a GAMSPy max-coverage LP to pick best tiles.
    """
    max_tiles = int(citizen_budget // road_cost)
    if max_tiles <= 0:
        return []

    # Identify target houses and their weights
    target_houses = []
    weights = {}
    for house in houses:
        hid = house["id"]
        pref = preferred_facility.get(hid)
        if pref is None:
            target_houses.append(house)
            weights[hid] = 10.0  # disconnected — HIGH priority
        elif pref == "Clinic":
            target_houses.append(house)
            weights[hid] = 2.0   # clinic — redirect to hospital

    if not target_houses:
        return []

    # For each target house, BFS to nearest facility through the grid.
    # Record which tiles on that path are NOT currently roads.
    # BFS treats all tiles as walkable for planning (city CAN build there).
    #
    # Disconnected houses consider ALL facilities (hospitals AND clinic) so
    # the optimizer always finds the cheapest reconnection regardless of which
    # facility is nearest.  Clinic-preferring houses still only target hospitals
    # (their goal is to be redirected away from the clinic).
    house_paths: dict[str, list[tuple[int, int]]] = {}

    for house in target_houses:
        hid = house["id"]
        hpos = (house["x"], house["y"])
        pref = preferred_facility.get(hid)
        best_path = None

        # Disconnected houses -> try every facility (hospitals + clinic).
        # Clinic-preferring houses -> only try hospitals (redirect goal).
        if pref is None:
            clinic_entry = [{"x": clinic_pos[0], "y": clinic_pos[1], "id": "Clinic"}] if clinic_pos else []
            facility_targets = hospitals + clinic_entry
        else:
            facility_targets = hospitals

        for fac in facility_targets:
            fac_pos = (fac["x"], fac["y"])
            path = bfs_any_path(hpos, fac_pos, occupied, grid_size)
            if path is None:
                continue
            new_tiles = [t for t in path if t not in all_roads and t not in occupied]
            if best_path is None or len(new_tiles) < len([t for t in best_path if t not in all_roads and t not in occupied]):
                best_path = path

        if best_path:
            new_tiles = [t for t in best_path if t not in all_roads and t not in occupied]
            if new_tiles:
                house_paths[hid] = new_tiles

    if not house_paths:
        return []

    # Collect all unique candidate tiles and map to houses they help
    candidate_tiles: dict[tuple[int, int], set[str]] = {}
    for hid, tiles in house_paths.items():
        for tile in tiles:
            if tile not in candidate_tiles:
                candidate_tiles[tile] = set()
            candidate_tiles[tile].add(hid)

    tile_list = list(candidate_tiles.keys())
    tile_ids  = [f"T{i}" for i in range(len(tile_list))]
    tile_index = {tile_ids[i]: tile_list[i] for i in range(len(tile_list))}

    target_hids = list(weights.keys())
    if not target_hids or not tile_ids:
        return []

    # need[h] = how many new tiles needed to complete path for house h
    need = {hid: max(1, len(house_paths.get(hid, []))) for hid in target_hids if hid in house_paths}
    target_hids = [hid for hid in target_hids if hid in need]

    if not target_hids:
        return []

    # ---- GAMSPy LP ----
    m2 = Container()

    r_set = Set(m2, name="r", records=tile_ids)
    h_set = Set(m2, name="h", records=target_hids)

    # benefit[r, h] = 1 if tile r is on the path for house h
    benefit_records = [
        (tid, hid, 1.0)
        for tid, tile in zip(tile_ids, tile_list)
        for hid in candidate_tiles.get(tile, set())
        if hid in need
    ]
    if not benefit_records:
        return []

    benefit = Parameter(m2, name="benefit", domain=[r_set, h_set], records=benefit_records)

    # Weight for each house
    w_records = [(hid, weights[hid]) for hid in target_hids]
    w = Parameter(m2, name="w", domain=h_set, records=w_records)

    # need_param[h] = 1/need[h] — scaling so z[h] = 1 only when all tiles built
    need_records = [(hid, 1.0 / need[hid]) for hid in target_hids]
    need_param = Parameter(m2, name="need_inv", domain=h_set, records=need_records)

    y = Variable(m2, name="y", domain=r_set, type="positive")  # build tile
    z = Variable(m2, name="z", domain=h_set, type="positive")  # house covered

    # Budget
    budget_eq = Equation(m2, name="budget_eq")
    budget_eq[...] = Sum(r_set, y[r_set]) <= max_tiles

    y_cap = Equation(m2, name="y_cap", domain=r_set)
    y_cap[r_set] = y[r_set] <= 1

    # z[h] ≤ need_inv[h] * Σ_r benefit[r,h] * y[r]
    # z reaches 1 only when all needed tiles for h are built
    z_bound = Equation(m2, name="z_bound", domain=h_set)
    z_bound[h_set] = z[h_set] <= need_param[h_set] * Sum(r_set, benefit[r_set, h_set] * y[r_set])

    z_cap = Equation(m2, name="z_cap", domain=h_set)
    z_cap[h_set] = z[h_set] <= 1

    # Objective: max weighted coverage
    obj2 = Variable(m2, name="obj2")
    obj2_eq = Equation(m2, name="obj2_eq")
    obj2_eq[...] = obj2 == Sum(h_set, w[h_set] * z[h_set])

    lp2 = Model(
        m2, name="city_optimizer",
        equations=[budget_eq, y_cap, z_bound, z_cap, obj2_eq],
        problem=Problem.LP, sense=Sense.MAX, objective=obj2,
    )
    lp2.solve()

    # Extract selected tiles
    selected: list[tuple[int, int]] = []
    try:
        y_vals = y.records
        if y_vals is not None and not y_vals.empty:
            sorted_tiles = y_vals.sort_values(by=y_vals.columns[1], ascending=False)
            spent = 0
            for _, row in sorted_tiles.iterrows():
                if spent >= max_tiles:
                    break
                if row.iloc[1] > 0.01:
                    tid = row.iloc[0]
                    tile = tile_index.get(tid)
                    if tile and tile not in all_roads:
                        selected.append(tile)
                        spent += 1
    except Exception:
        # Greedy fallback: tiles helping the most houses
        scored = sorted(candidate_tiles.keys(), key=lambda t: -len(candidate_tiles[t]))
        selected = [t for t in scored if t not in all_roads][:max_tiles]

    return selected


def bfs_any_path(start, goal, occupied, grid_size):
    """
    BFS on full grid (all tiles walkable) to find shortest tile path
    from start to goal.  Used for city road planning only.
    """
    if start == goal:
        return []
    visited = {start}
    queue = deque([(start, [])])
    while queue:
        pos, path = queue.popleft()
        for dx, dy in CARDINAL_STEPS:
            nxt = (pos[0] + dx, pos[1] + dy)
            if nxt[0] < 0 or nxt[1] < 0 or nxt[0] >= grid_size or nxt[1] >= grid_size:
                continue
            if nxt in visited:
                continue
            new_path = path + [nxt]
            if nxt == goal:
                return new_path
            # Can walk through for planning — tiles between buildings
            # but skip occupied tiles (buildings) mid-path
            if nxt in occupied and nxt != goal:
                visited.add(nxt)
                continue
            visited.add(nxt)
            queue.append((nxt, new_path))
    return None


# ---------------------------------------------------------------------------
# Setup-phase road network construction (BFS, clinic-first)
# ---------------------------------------------------------------------------

def build_road_network(
    houses, facilities, existing_roads, budget, road_cost, setup_phase,
) -> list[tuple[int, int]]:
    all_entities = [*houses, *facilities]
    occupied = {(int(e["x"]), int(e["y"])) for e in all_entities}

    clinic = next((f for f in facilities if f["id"] == "Clinic"), None)
    if clinic is None:
        return []

    clinic_pos = (clinic["x"], clinic["y"])
    connectors = get_connectors(clinic_pos, occupied, 22, 22)
    if not connectors:
        return []

    road_network = set(existing_roads)
    built_roads: list[tuple[int, int]] = []

    if not road_network:
        seed = connectors[0]
        road_network.add(seed)
        built_roads.append(seed)

    targets = [e for e in all_entities if (e["x"], e["y"]) != clinic_pos]
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


def get_connectors(origin, occupied, max_x, max_y):
    ox, oy = origin
    return [
        (ox + dx, oy + dy)
        for dx, dy in CARDINAL_STEPS
        if 0 <= ox + dx <= max_x
        and 0 <= oy + dy <= max_y
        and (ox + dx, oy + dy) not in occupied
    ]


def shortest_connector_path(connectors, road_network, occupied, max_x, max_y):
    best = None
    for connector in connectors:
        path = shortest_path_to_network(connector, road_network, occupied, max_x, max_y)
        if path is None:
            continue
        if best is None or len(path) < len(best):
            best = path
    return best


def shortest_path_to_network(start, road_network, occupied, max_x, max_y):
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
