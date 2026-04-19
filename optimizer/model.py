from gamspy import Container, Set, Parameter, Variable, Equation, Model, Problem, Sense, Sum

def solve_turn(data: dict) -> dict:
    houses = data["houses"]
    access = data["house_access"]
    budget = float(data["budget"])

    # Pick cheapest facility for each house
    best_by_house = {}
    for row in access:
        house = row["house"]
        facility = row["facility"]
        cost = float(row["travel_cost"])

        if house not in best_by_house or cost < best_by_house[house][1]:
            best_by_house[house] = (facility, cost)

    clinic_count = sum(
        1 for house in houses
        if best_by_house[house][0] == "Clinic"
    )
    clinic_ratio = clinic_count / max(1, len(houses))

    # Minimal GAMSPy model: total citizen cost
    m = Container()
    h = Set(m, name="h", records=houses)

    cost_data = [(house, best_by_house[house][1]) for house in houses]
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
            house: best_by_house[house][0] for house in houses
        },
        "clinic_count": clinic_count,
        "clinic_ratio": clinic_ratio,
        "clinic_alive": clinic_ratio >= 0.5,
        "citizen_cost_objective": float(model.objective_value or 0.0),
        "solver_status": str(model.solve_status),
        "budget_seen": budget,
    }