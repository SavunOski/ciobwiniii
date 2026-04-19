## Hackathon Spec: Clinic Survival

### Overview

**Clinic Survival** is a city-building optimization game where the player must keep an inefficient clinic operational by manipulating the city’s road network. Citizens always choose the healthcare facility that is most efficient for them to reach. The player’s job is to bias the network so that the **clinic remains the preferred destination for at least 50% of houses**, even though the city’s natural optimization would favor larger hospitals.

The game is designed to teach **network optimization, shortest-path reasoning, congestion management, and adversarial optimization** in a simple, interactive format.

---

### Core Premise

A city contains:

* **1 Clinic** (the fragile facility the player wants to preserve)
* **2+ Hospitals** (the naturally better options)
* **Multiple Houses** (starting nodes where citizens originate)
* **Roads** with travel time and congestion values
* Optional **obstacles** such as lakes or mountains

At the start of each day, every house evaluates all healthcare facilities and chooses the one with the **lowest route cost**.

The clinic is considered **alive** if it is the most efficient destination for **at least 50% of houses**.

---

### Player Goal

Keep the clinic alive for as many days as possible.

The player wins by maximizing:

* **Days survived**
* **Clinic preference coverage**
* **Revenue generated from clinic usage**

The player loses if:

* Fewer than **50% of houses** prefer the clinic at the end of a day, or
* The player runs out of budget and can no longer make meaningful interventions

---

### Educational Optimization Concepts

This game teaches:

* **Shortest Path Optimization**
  Citizens choose the facility with the lowest total travel cost.

* **Congestion-Aware Routing**
  Roads become less attractive as traffic increases.

* **Network Design Optimization**
  Players reshape the road system to alter citizen behavior.

* **Multi-objective Optimization**
  Balance clinic survival, budget, and travel efficiency.

* **Adversarial Optimization**
  The player biases the city toward the clinic, while the city optimizer restores globally efficient routing.

---

### Core Gameplay Loop

#### 1. Daily Baseline Calculation

GAMSPy computes:

* Optimal route cost from each house to each hospital and the clinic
* Citizen facility choice based on route cost
* Current congestion effects
* Whether the clinic survives this day

#### 2. Player Intervention Phase

The player spends budget to manipulate the city in favor of the clinic through actions such as:

* Upgrade roads leading to clinic
* Degrade roads leading to hospitals
* Add traffic lights or congestion points
* Temporarily block roads
* Trigger powers that weaken hospital access

#### 3. Citizen Routing Phase

Each house selects the healthcare facility with the lowest route cost.

#### 4. Revenue Phase

The clinic earns revenue based on how many houses prefer it.
To keep the game fair, the clinic earns **higher revenue per assigned house** than hospitals would.

#### 5. Opposing Optimization Phase

The city’s optimizer uses its own budget to improve roads for citizen welfare, which typically makes hospitals more attractive again.

This repeats each day until the clinic fails or the player runs out of options.

---

### Facility Choice Model

For each house:

**Route Cost = Path Distance + Congestion Penalty + Small Pricing Modifier**

Where:

* **Path Distance** = shortest-path travel time through the road network
* **Congestion Penalty** = extra delay caused by road usage vs capacity
* **Pricing Modifier** = low-priority factor to break ties or slightly influence demand

A house chooses the facility with the **lowest route cost**.

---

### Survival Rule

Let:

* `H` = total number of houses
* `C` = number of houses that prefer the clinic

The clinic survives if:

**C / H >= 0.5**

This makes the clinic’s viability based on **coverage**, not fixed patient counts.

---

### Economy

* The player starts with a limited budget.
* Each day, the clinic generates income based on its coverage.
* More houses preferring the clinic means more money for future interventions.
* The player must spend carefully because the city optimizer also acts every turn.

This creates a feedback loop:

* Better clinic coverage → more money → more control next day
* Poor coverage → less money → harder recovery

---

### Map Generation Rules

* There must always be **more houses than healthcare facilities**
* The **clinic has an exclusion radius** where hospitals cannot spawn
* Hospitals have a **spawn buffer** where houses cannot spawn too close
* Obstacles may lengthen routes but **cannot make any building inaccessible**
* Every house must have at least one valid path to every facility
* The clinic should begin **slightly disadvantaged**, but not impossible to save

---

### Sample Player Actions

Positive actions:

* Upgrade clinic road
* Build shortcut to clinic
* Increase road capacity near clinic
* Lower clinic pricing temporarily

Negative actions:

* Downgrade hospital road
* Add traffic light near hospital corridor
* Create congestion hotspot
* Temporarily block a hospital route

Special powers:

* Cut hospital funding
* Emergency detour
* Temporary roadworks
* One-turn traffic surge

---

### Opposing Optimizer Behavior

The AI optimizer represents the city acting in the public’s best interest.

It tries to:

* Reduce average route cost
* Reduce congestion
* Improve highly used roads
* Restore damaged traffic efficiency

It does **not** target the clinic directly.
Its goal is simply to make the overall network more efficient for citizens.

---

### Minimal Prototype Scope

For the hackathon MVP:

* 1 clinic
* 2 hospitals
* 8 houses
* Weighted road graph
* Daily recalculation of preferred facility per house
* Budget-based player interventions
* Opposing road optimization each turn
* Clinic survives if preferred by at least 4 of 8 houses

---

### Technical Implementation

**Engine:** Godot
**Optimization Backend:** GAMSPy

**Workflow:**

* Godot stores the map, houses, roads, and UI
* Godot sends the current city state to GAMSPy
* GAMSPy calculates:

  * shortest route costs
  * house-to-facility assignment
  * congestion-adjusted preference
  * recommended road optimizations
* Results are returned to Godot and visualized in-game

---

### Why This Works

This concept is strong because it turns a serious optimization problem into a simple, legible game system:

* Citizens follow the cheapest route
* Roads shape behavior
* The player manipulates infrastructure
* A neutral optimizer pushes back
* Survival depends on understanding optimization better than the system

It teaches optimization through direct play, without requiring players to know formal math in advance.