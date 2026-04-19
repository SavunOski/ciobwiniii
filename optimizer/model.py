from collections import deque

from gamspy import Container, Equation, Model, Parameter, Problem, Sense, Set, Sum, Variable


CARDINAL_STEPS = ((0, -1), (1, 0), (0, 1), (-1, 0))


def solve_turn(data: dict) -> dict:
    houses = data["houses"]
    facilities = data["facilities"]
    access = data["house_access"]
    budget = float(data["budget"])
    road_cost = max(float(data.get("road_cost", 3.0)), 0.0001)
    setup_phase = bool(data.get("setup_phase", False))
    existing_roads = {
        (int(point["x"]), int(point["y"])) for point in data.get("existing_roads", [])
    }

    house_ids = [house["id"] for house in houses]

    best_by_house = {}
    for row in access:
        house = row["house"]
        facility = row["facility"]
        cost = float(row["travel_cost"])

        if house not in best_by_house or cost < best_by_house[house][1]:
            best_by_house[house] = (facility, cost)

    clinic_count = sum(
        1 for house in house_ids if best_by_house.get(house, ("", 0.0))[0] == "Clinic"
    )
    clinic_ratio = clinic_count / max(1, len(house_ids))

    built_roads = build_road_network(
        houses=houses,
        facilities=facilities,
        existing_roads=existing_roads,
        budget=budget,
        road_cost=road_cost,
        setup_phase=setup_phase,
    )

    spent_budget = 0.0 if setup_phase else len(built_roads) * road_cost

    m = Container()
    h = Set(m, name="h", records=house_ids)

    cost_data = [(house, best_by_house[house][1]) for house in house_ids if house in best_by_house]
    cost = Parameter(m, name="cost", domain=h, records=cost_data)

    z = Variable(m, name="z")
    total_cost_eq = Equation(m, name="total_cost_eq")
    total_cost_eq[...] = z == Sum(h, cost[h])

    model = Model(
        m,
        name="city_routing_eval",
        equations=[total_cost_eq],
        problem=Problem.LP,
        sense=Sense.MIN,
        objective=z,
    )
    model.solve()

    return {
        "preferred_facility": {
            house: best_by_house[house][0] for house in house_ids if house in best_by_house
        },
        "clinic_count": clinic_count,
        "clinic_ratio": clinic_ratio,
        "clinic_alive": clinic_ratio >= 0.5,
        "citizen_cost_objective": float(model.objective_value or 0.0),
        "solver_status": str(model.solve_status),
        "budget_seen": budget,
        "spent_budget": spent_budget,
        "built_roads": [{"x": x, "y": y} for x, y in built_roads],
    }


def build_road_network(
    houses: list[dict],
    facilities: list[dict],
    existing_roads: set[tuple[int, int]],
    budget: float,
    road_cost: float,
    setup_phase: bool,
) -> list[tuple[int, int]]:
    occupied = {
        (int(entity["x"]), int(entity["y"])) for entity in [*houses, *facilities]
    }
    max_x = max(x for x, _ in occupied) + 2
    max_y = max(y for _, y in occupied) + 2

    clinic = next((facility for facility in facilities if facility["id"] == "Clinic"), None)
    if clinic is None:
        return []

    clinic_connectors = get_connectors((clinic["x"], clinic["y"]), occupied, max_x, max_y)
    if not clinic_connectors:
        return []

    road_network = set(existing_roads)
    built_roads: list[tuple[int, int]] = []

    if not road_network:
        seed = clinic_connectors[0]
        road_network.add(seed)
        built_roads.append(seed)

    targets = []
    for facility in facilities:
        if facility["id"] != "Clinic":
            targets.append(facility)
    targets.extend(houses)

    remaining_budget_tiles = None if setup_phase else int(budget // road_cost)
    if remaining_budget_tiles is not None:
        remaining_budget_tiles = max(0, remaining_budget_tiles)

    pending = list(targets)
    while pending:
        options = []
        for entity in pending:
            connectors = get_connectors((entity["x"], entity["y"]), occupied, max_x, max_y)
            path = shortest_connector_path(connectors, road_network, occupied, max_x, max_y)
            if path is None:
                continue

            missing_tiles = [tile for tile in path if tile not in road_network]
            options.append((len(missing_tiles), entity["id"], entity, missing_tiles))

        if not options:
            break

        options.sort(key=lambda item: (item[0], item[1]))

        chosen = None
        for option in options:
            missing_tiles = option[3]
            if remaining_budget_tiles is None or len(missing_tiles) <= remaining_budget_tiles:
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

        if remaining_budget_tiles is not None:
            remaining_budget_tiles -= len(missing_tiles)

    return built_roads


def get_connectors(
    origin: tuple[int, int],
    occupied: set[tuple[int, int]],
    max_x: int,
    max_y: int,
) -> list[tuple[int, int]]:
    ox, oy = origin
    connectors = []
    for dx, dy in CARDINAL_STEPS:
        point = (ox + dx, oy + dy)
        if point[0] < 0 or point[1] < 0 or point[0] > max_x or point[1] > max_y:
            continue
        if point in occupied:
            continue
        connectors.append(point)
    return connectors


def shortest_connector_path(
    connectors: list[tuple[int, int]],
    road_network: set[tuple[int, int]],
    occupied: set[tuple[int, int]],
    max_x: int,
    max_y: int,
) -> list[tuple[int, int]] | None:
    best_path = None
    for connector in connectors:
        path = shortest_path_to_network(connector, road_network, occupied, max_x, max_y)
        if path is None:
            continue
        if best_path is None or len(path) < len(best_path):
            best_path = path
    return best_path


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
