from fastapi import FastAPI
from pydantic import BaseModel, Field
from typing import List
from model import solve_turn

app = FastAPI()

class GridPoint(BaseModel):
    x: int
    y: int


class HouseData(BaseModel):
    id: str
    x: int
    y: int


class FacilityData(BaseModel):
    id: str
    type: str
    x: int
    y: int

class FacilityCost(BaseModel):
    house: str
    facility: str
    travel_cost: float

class SolveRequest(BaseModel):
    houses: List[HouseData]
    facilities: List[FacilityData]
    house_access: List[FacilityCost]
    budget: float
    existing_roads: List[GridPoint] = Field(default_factory=list)
    road_cost: float = 3.0
    turn_index: int = 0
    setup_phase: bool = False

@app.post("/solve")
def solve(data: SolveRequest):
    return solve_turn(data.model_dump())
