from fastapi import FastAPI
from pydantic import BaseModel
from typing import List
from model import solve_turn

app = FastAPI()

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
    houses: List[str]
    facilities: List[FacilityData]
    house_access: List[FacilityCost]
    budget: float

@app.post("/solve")
def solve(data: SolveRequest):
    return solve_turn(data.model_dump())