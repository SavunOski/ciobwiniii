using Godot;
using System.Collections.Generic;

public partial class Main : Node2D
{
	private Camera2D _camera;
	private bool _isDragging = false;
	private const int GridSize = 20;
	private const int TileSpacing = 64;
	private readonly HashSet<Vector2> _occupiedTiles = new();

	public override void _Ready()
	{
		_camera = GetNode<Camera2D>("Camera2D");
		_camera.MakeCurrent(); // Fixed: Must be inside a method
		
		RenderingServer.SetDefaultClearColor(new Color(0.1f, 0.1f, 0.1f)); 
		GenerateGrid();
	}

	private void GenerateGrid()
	{
		var rng = new RandomNumberGenerator();
		rng.Randomize();

		// 1. Ground
		for (int x = 0; x < GridSize; x++)
		{
			for (int y = 0; y < GridSize; y++)
			{
				Color groundColor = rng.Randf() < 0.10f ? new Color(0.3f, 0.3f, 0.3f) : new Color(0.2f, 0.5f, 0.2f);
				if (groundColor.R > 0.2f) _occupiedTiles.Add(new Vector2(x, y)); 
				PlaceTile(new Vector2(x, y), groundColor, $"Tile_{x}_{y}");
			}
		}

		// 2. Clinic
		Vector2 clinicPos = new(GridSize / 2, GridSize / 2);
		PlaceEntity(clinicPos, new Color(0.8f, 0.6f, 0.2f), "🌟", "Clinic");
		_occupiedTiles.Add(clinicPos);

		// 3. Hospitals
		PlaceRandomEntities(rng, 4, 12f, new List<Vector2> { clinicPos }, new Color(0.8f, 0.2f, 0.2f), "🏥", "Hospital");

		// 4. Houses
		PlaceRandomEntities(rng, 6, 8f, null, new Color(0.2f, 0.4f, 0.8f), "🏠", "House");
	}

	private void PlaceRandomEntities(RandomNumberGenerator rng, int count, float minDist, List<Vector2> exclusions, Color col, string emoji, string prefix)
	{
		int placed = 0;
		int attempts = 0;
		while (placed < count && attempts < 1000)
		{
			attempts++;
			Vector2 pos = new(rng.RandiRange(2, GridSize - 3), rng.RandiRange(2, GridSize - 3));
			if (_occupiedTiles.Contains(pos)) continue;

			bool tooClose = false;
			if (exclusions != null) {
				foreach (var ex in exclusions) if (pos.DistanceTo(ex) < minDist) tooClose = true;
			}

			if (!tooClose) {
				PlaceEntity(pos, col, emoji, $"{prefix}_{placed}");
				_occupiedTiles.Add(pos);
				placed++;
			}
		}
	}

	private void PlaceTile(Vector2 gridPos, Color color, string name)
	{
		var rect = new ColorRect {
			Size = new Vector2(TileSpacing - 2, TileSpacing - 2),
			Position = gridPos * TileSpacing,
			Color = color,
			Name = name,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			ZIndex = 0 
		};
		AddChild(rect);
	}

	private void PlaceEntity(Vector2 gridPos, Color bgColor, string emoji, string nodeName)
	{
		var rect = new ColorRect {
			Size = new Vector2(TileSpacing - 2, TileSpacing - 2),
			Position = gridPos * TileSpacing,
			Color = bgColor,
			Name = nodeName,
			ZIndex = 1 // Ensures visibility over ground
		};

		var label = new Label {
			Text = emoji,
			Size = rect.Size,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		label.AddThemeFontSizeOverride("font_size", 32);
		
		rect.AddChild(label);
		AddChild(rect);
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton mb)
		{
			// Zooming
			if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
				_camera.Zoom *= 1.1f;
			if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
				_camera.Zoom *= 0.9f;

			// Panning start/stop
			if (mb.ButtonIndex == MouseButton.Middle)
				_isDragging = mb.Pressed;
		}
		// Panning motion
		else if (@event is InputEventMouseMotion mm && _isDragging)
		{
			_camera.Position -= mm.Relative / _camera.Zoom;
		}
	}
}
