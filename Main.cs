using Godot;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

public partial class Main : Node2D
{
	private Camera2D _camera;
	private HTTPRequest _http;
	private bool _isDragging = false;

	private const int GridSize = 20;
	private const int TileSpacing = 64;

	private readonly HashSet<Vector2I> _occupiedTiles = new();
	private readonly List<Vector2I> _hospitalPositions = new();
	private readonly List<Vector2I> _housePositions = new();
	private Vector2I _clinicPos;

	public override void _Ready()
	{
		_camera = GetNode<Camera2D>("Camera2D");
		_camera.MakeCurrent();

		_http = new HTTPRequest();
		AddChild(_http);
		_http.RequestCompleted += OnRequestCompleted;

		RenderingServer.SetDefaultClearColor(new Color(0.1f, 0.1f, 0.1f));
		GenerateGrid();

		CallDeferred(nameof(SendInitialOptimizationRequest));
	}

	private void GenerateGrid()
	{
		var rng = new RandomNumberGenerator();
		rng.Randomize();

		// Ground
		for (int x = 0; x < GridSize; x++)
		{
			for (int y = 0; y < GridSize; y++)
			{
				Color groundColor = rng.Randf() < 0.10f
					? new Color(0.3f, 0.3f, 0.3f)
					: new Color(0.2f, 0.5f, 0.2f);

				PlaceTile(new Vector2I(x, y), groundColor, $"Tile_{x}_{y}");
			}
		}

		// Clinic
		_clinicPos = new Vector2I(GridSize / 2, GridSize / 2);
		PlaceEntity(_clinicPos, new Color(0.8f, 0.6f, 0.2f), "🌟", "Clinic");
		_occupiedTiles.Add(_clinicPos);

		// Hospitals
		PlaceRandomEntities(
			rng,
			count: 4,
			minDist: 12f,
			exclusions: new List<Vector2I> { _clinicPos },
			col: new Color(0.8f, 0.2f, 0.2f),
			emoji: "🏥",
			prefix: "Hospital",
			storeList: _hospitalPositions
		);

		// Houses
		PlaceRandomEntities(
			rng,
			count: 6,
			minDist: 8f,
			exclusions: null,
			col: new Color(0.2f, 0.4f, 0.8f),
			emoji: "🏠",
			prefix: "House",
			storeList: _housePositions
		);
	}

	private void PlaceRandomEntities(
		RandomNumberGenerator rng,
		int count,
		float minDist,
		List<Vector2I> exclusions,
		Color col,
		string emoji,
		string prefix,
		List<Vector2I> storeList
	)
	{
		int placed = 0;
		int attempts = 0;

		while (placed < count && attempts < 1000)
		{
			attempts++;
			Vector2I pos = new Vector2I(
				rng.RandiRange(2, GridSize - 3),
				rng.RandiRange(2, GridSize - 3)
			);

			if (_occupiedTiles.Contains(pos))
				continue;

			bool tooClose = false;
			if (exclusions != null)
			{
				foreach (var ex in exclusions)
				{
					if (Distance(pos, ex) < minDist)
					{
						tooClose = true;
						break;
					}
				}
			}

			if (!tooClose)
			{
				string nodeName = $"{prefix}_{placed}";
				PlaceEntity(pos, col, emoji, nodeName);
				_occupiedTiles.Add(pos);
				storeList.Add(pos);
				placed++;
			}
		}
	}

	private void PlaceTile(Vector2I gridPos, Color color, string name)
	{
		var rect = new ColorRect
		{
			Size = new Vector2(TileSpacing - 2, TileSpacing - 2),
			Position = new Vector2(gridPos.X * TileSpacing, gridPos.Y * TileSpacing),
			Color = color,
			Name = name,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			ZIndex = 0
		};

		AddChild(rect);
	}

	private void PlaceEntity(Vector2I gridPos, Color bgColor, string emoji, string nodeName)
	{
		var rect = new ColorRect
		{
			Size = new Vector2(TileSpacing - 2, TileSpacing - 2),
			Position = new Vector2(gridPos.X * TileSpacing, gridPos.Y * TileSpacing),
			Color = bgColor,
			Name = nodeName,
			ZIndex = 1
		};

		var label = new Label
		{
			Text = emoji,
			Size = rect.Size,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		label.AddThemeFontSizeOverride("font_size", 32);

		rect.AddChild(label);
		AddChild(rect);
	}

	private void SendInitialOptimizationRequest()
	{
		var payload = BuildOptimizationPayload();
		string json = JsonSerializer.Serialize(payload);
		string[] headers = { "Content-Type: application/json" };

		Error err = _http.Request(
			"http://127.0.0.1:8000/solve",
			headers,
			HttpClient.Method.Post,
			json
		);

		if (err != Error.Ok)
		{
			GD.PrintErr($"Failed to send optimization request: {err}");
		}
		else
		{
			GD.Print("Sent optimization request:");
			GD.Print(json);
		}
	}

	private SolveRequest BuildOptimizationPayload()
	{
		var facilities = new List<FacilityData>
		{
			new FacilityData
			{
				id = "Clinic",
				type = "clinic",
				x = _clinicPos.X,
				y = _clinicPos.Y
			}
		};

		for (int i = 0; i < _hospitalPositions.Count; i++)
		{
			var hp = _hospitalPositions[i];
			facilities.Add(new FacilityData
			{
				id = $"Hospital_{i}",
				type = "hospital",
				x = hp.X,
				y = hp.Y
			});
		}

		var houses = new List<string>();
		var houseAccess = new List<FacilityCost>();

		for (int i = 0; i < _housePositions.Count; i++)
		{
			string houseId = $"House_{i}";
			Vector2I housePos = _housePositions[i];
			houses.Add(houseId);

			// Clinic cost
			houseAccess.Add(new FacilityCost
			{
				house = houseId,
				facility = "Clinic",
				travel_cost = ComputeTravelCost(housePos, _clinicPos, isClinic: true)
			});

			// Hospital costs
			for (int h = 0; h < _hospitalPositions.Count; h++)
			{
				houseAccess.Add(new FacilityCost
				{
					house = houseId,
					facility = $"Hospital_{h}",
					travel_cost = ComputeTravelCost(housePos, _hospitalPositions[h], isClinic: false)
				});
			}
		}

		return new SolveRequest
		{
			houses = houses,
			facilities = facilities,
			house_access = houseAccess,
			budget = 20.0f
		};
	}

	private float ComputeTravelCost(Vector2I from, Vector2I to, bool isClinic)
	{
		// Prototype version:
		// Manhattan distance + light random congestion.
		// Slightly bias against clinic to reflect "optimally should shut down".
		float dist = Mathf.Abs(from.X - to.X) + Mathf.Abs(from.Y - to.Y);
		float congestion = ((from.X + from.Y + to.X + to.Y) % 4) * 0.5f;
		float clinicPenalty = isClinic ? 1.5f : 0.0f;

		return dist + congestion + clinicPenalty;
	}

	private float Distance(Vector2I a, Vector2I b)
	{
		return Mathf.Sqrt(Mathf.Pow(a.X - b.X, 2) + Mathf.Pow(a.Y - b.Y, 2));
	}

	private void OnRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
	{
		string responseText = Encoding.UTF8.GetString(body);
		GD.Print($"Optimizer response code: {responseCode}");
		GD.Print(responseText);

		try
		{
			var response = JsonSerializer.Deserialize<SolveResponse>(responseText);
			if (response != null)
			{
				GD.Print($"Clinic alive: {response.clinic_alive}");
				GD.Print($"Clinic ratio: {response.clinic_ratio}");
			}
		}
		catch (Exception e)
		{
			GD.PrintErr($"Failed to parse optimizer response: {e.Message}");
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb)
		{
			if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
				_camera.Zoom *= 1.1f;
			if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
				_camera.Zoom *= 0.9f;

			if (mb.ButtonIndex == MouseButton.Middle)
				_isDragging = mb.Pressed;
		}
		else if (@event is InputEventMouseMotion mm && _isDragging)
		{
			_camera.Position -= mm.Relative / _camera.Zoom;
		}
	}
}

// Request/response models

public class SolveRequest
{
	public List<string> houses { get; set; }
	public List<FacilityData> facilities { get; set; }
	public List<FacilityCost> house_access { get; set; }
	public float budget { get; set; }
}

public class FacilityData
{
	public string id { get; set; }
	public string type { get; set; }
	public int x { get; set; }
	public int y { get; set; }
}

public class FacilityCost
{
	public string house { get; set; }
	public string facility { get; set; }
	public float travel_cost { get; set; }
}

public class SolveResponse
{
	public Dictionary<string, string> preferred_facility { get; set; }
	public int clinic_count { get; set; }
	public float clinic_ratio { get; set; }
	public bool clinic_alive { get; set; }
	public float citizen_cost_objective { get; set; }
	public string solver_status { get; set; }
	public float budget_seen { get; set; }
}