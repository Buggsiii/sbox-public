using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Editor;

public static partial class EditorUtility
{
	/// <summary>
	/// Create a vmdl file from a mesh. Will return non null if the asset was created successfully
	/// </summary>
	public static unsafe Asset CreateModelFromMeshFile( Asset meshFile, string targetAbsolutePath = null, IEnumerable<string> excludedParts = null, bool excludeByDefault = false )
	{
		var modelFilename = targetAbsolutePath ?? System.IO.Path.ChangeExtension( meshFile.GetSourceFile( true ), ".vmdl" );

		if ( System.IO.File.Exists( modelFilename ) )
			return null;

		// In the future we could just init all tools upfront
		if ( !g_pToolFramework2.InitEngineTool( "modeldoc_editor" ) )
			return null;

		var document = CModelDoc.Create();
		g_pModelDocUtils.InitFromMesh( document, meshFile.Path );

		var modelDirectory = System.IO.Path.GetDirectoryName( modelFilename );
		if ( !string.IsNullOrWhiteSpace( modelDirectory ) && !System.IO.Directory.Exists( modelDirectory ) )
			System.IO.Directory.CreateDirectory( modelDirectory );

		document.SaveToFile( modelFilename );
		document.DeleteThis();

		var asset = AssetSystem.RegisterFile( modelFilename );
		if ( asset is null )
		{
			var relativeModelFilename = FileSystem.Content.GetRelativePath( modelFilename );
			if ( !string.IsNullOrWhiteSpace( relativeModelFilename ) )
			{
				relativeModelFilename = relativeModelFilename.Replace( '\\', '/' ).TrimStart( '/' );
				asset = AssetSystem.RegisterFile( relativeModelFilename );
			}
		}

		if ( asset is null )
		{
			Log.Info( $"Asset is null! modelFilename={modelFilename}" );
			return null;
		}

		bool shouldApplyFilter = excludedParts is not null && excludedParts.Any();
		if ( shouldApplyFilter )
		{
			Log.Info( "Applying filter." );
			AddImportFilterExceptionList( modelFilename, excludedParts, excludeByDefault );
		}

		asset.Compile( true );

		return asset;
	}

	private static void AddImportFilterExceptionList( string vmdlPath, IEnumerable<string> exceptionList, bool excludeByDefault = false )
	{
		Log.Info( "Applying filter" );
		Log.Info( exceptionList );
		Log.Info( excludeByDefault );
		string text = System.IO.File.ReadAllText( vmdlPath );
		string marker = "import_filter";
		string insertBlock = $"import_filter =\r\n{{\r\n    exclude_by_default = {excludeByDefault.ToString().ToLowerInvariant()}\r\n    exception_list =\r\n    [\r\n{string.Join( "\r\n", exceptionList.Select( n => "        \"" + n.Replace( "\"", "\\\"" ) + "\"" ) )}\r\n    ]\r\n}}\r\n";

		if ( text.Contains( marker, StringComparison.OrdinalIgnoreCase ) )
		{
			// replace existing import_filter block in RenderMeshFile if present (best-effort)
			int start = text.IndexOf( marker, StringComparison.OrdinalIgnoreCase );
			int brace = text.IndexOf( '{', start );
			if ( brace >= 0 )
			{
				int level = 0;
				int end = -1;
				for ( int i = brace; i < text.Length; i++ )
				{
					if ( text[i] == '{' ) level++;
					else if ( text[i] == '}' ) level--;
					if ( level == 0 )
					{
						end = i;
						break;
					}
				}

				if ( end >= 0 )
				{
					text = string.Concat( text.AsSpan( 0, start ), insertBlock, text.AsSpan( end + 1 ) );
				}
				else
				{
					text += "\r\n" + insertBlock;
				}
			}
			else
			{
				text += "\r\n" + insertBlock;
			}
		}
		else
		{
			text += "\r\n" + insertBlock;
		}

		System.IO.File.WriteAllText( vmdlPath, text, System.Text.Encoding.UTF8 );
	}

	private static string[] ParseImportFilterExceptionList( string vmdlPath )
	{
		if ( !System.IO.File.Exists( vmdlPath ) ) return [];

		var text = System.IO.File.ReadAllText( vmdlPath );
		var marker = "exception_list";
		var start = text.IndexOf( marker, StringComparison.OrdinalIgnoreCase );
		if ( start < 0 ) return [];

		var bracketStart = text.IndexOf( '[', start );
		if ( bracketStart < 0 ) return [];

		var bracketEnd = text.IndexOf( ']', bracketStart );
		if ( bracketEnd < 0 ) return [];

		var block = text[(bracketStart + 1)..bracketEnd];

		var entries = block
			.Split( ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries )
			.Select( line => line.Trim() )
			.Select( line =>
			{
				line = line.TrimEnd( ',' ).Trim();
				if ( line.StartsWith( '"' ) && line.EndsWith( '"' ) && line.Length >= 2 )
					line = line[1..^1];
				return line.Replace( "\\\"", "\"" ).Trim();
			} )
			.Where( line => !string.IsNullOrWhiteSpace( line ) )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.ToArray();

		return entries;
	}

	public static string[] GetMeshPartsFromMeshFile( Asset meshFile, string targetAbsolutePath = null )
	{
		string sourceFile = meshFile.GetSourceFile( true );
		string modelFilename = targetAbsolutePath ?? System.IO.Path.ChangeExtension( sourceFile, ".vmdl" );

		if ( !System.IO.File.Exists( modelFilename ) )
			_ = CreateModelFromMeshFile( meshFile, modelFilename );

		if ( !System.IO.File.Exists( modelFilename ) )
		{
			Log.Info( "File does not exist!" );
			return [];
		}

		var partsFromVmdl = ParseImportFilterExceptionList( modelFilename );
		return partsFromVmdl.Length > 0 ? partsFromVmdl : [];
	}

	public static unsafe Asset CreatePrefabFromMeshFile( Asset meshFile, string targetAbsolutePath = null )
	{
		var sourceFile = meshFile.GetSourceFile( true );
		var folderPath = System.IO.Path.Combine( System.IO.Path.GetDirectoryName( sourceFile ) ?? string.Empty, System.IO.Path.GetFileNameWithoutExtension( sourceFile ) );

		var partNames = GetMeshPartsFromMeshFile( meshFile );
		if ( partNames.Length == 0 )
			return CreateModelFromMeshFile( meshFile, null );

		if ( !System.IO.Directory.Exists( folderPath ) )
			System.IO.Directory.CreateDirectory( folderPath );

		var partModels = new List<(string name, string path)>();
		foreach ( var part in partNames )
		{
			var safe = MakeSafeFilename( part );
			var partVmdl = System.IO.Path.Combine( folderPath, $"{System.IO.Path.GetFileNameWithoutExtension( sourceFile )}_{safe}.vmdl" );
			var partAsset = CreateModelFromMeshFile( meshFile, partVmdl, [part], excludeByDefault: true );
			if ( partAsset != null )
				partModels.Add( (name: part, path: partAsset.Path ?? partVmdl) );
		}

		var prefabFilename = targetAbsolutePath ?? System.IO.Path.ChangeExtension( sourceFile, ".prefab" );
		var source = new PrefabFile();
		source.RegisterWeakResourceId( prefabFilename );
		source.Register( prefabFilename );
		var prefab = new PrefabScene( true )
		{
			Source = source
		};

		GameObject root = null;
		using ( prefab.Push() )
		{
			root = new( prefab )
			{
				Name = System.IO.Path.GetFileNameWithoutExtension( prefabFilename )
			};

			foreach ( var (name, path) in partModels )
			{
				var partObj = new GameObject( prefab )
				{
					Name = name,
					Parent = root
				};

				var renderer = partObj.Components.Create<ModelRenderer>();
				renderer.Model = Model.Load( path );
			}
		}

		if ( root is null )
			return null;

		Prefabs.ConvertGameObjectToPrefab( root, prefabFilename );
		var prefabAsset = AssetSystem.RegisterFile( prefabFilename );
		prefabAsset?.Compile( true );

		return prefabAsset;
	}

	private static string MakeSafeFilename( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return "mesh";

		var invalidChars = System.IO.Path.GetInvalidFileNameChars();
		var safe = new string( [.. name.Select( c => invalidChars.Contains( c ) ? '_' : c )] );

		return string.IsNullOrWhiteSpace( safe ) ? "mesh" : safe;
	}

	/// <summary>
	/// Create a vmdl file from polygon meshes. Will return non null if the asset was created successfully
	/// </summary>
	public static unsafe Asset CreateModelFromPolygonMeshes( PolygonMesh[] polygonMeshes, string targetAbsolutePath )
	{
		if ( polygonMeshes is null )
			return null;

		if ( polygonMeshes.Length == 0 )
			return null;

		if ( string.IsNullOrWhiteSpace( targetAbsolutePath ) )
			return null;

		if ( !g_pToolFramework2.InitEngineTool( "modeldoc_editor" ) )
			return null;

		var meshes = new List<CModelMesh>();
		foreach ( var polygonMesh in polygonMeshes )
		{
			if ( polygonMesh is null )
				continue;

			var mesh = CModelMesh.Create();
			meshes.Add( mesh );

			var vertices = polygonMesh.VertexHandles.ToArray();
			mesh.AddVertices( vertices.Length );

			var materials = polygonMesh.Materials.ToArray();
			foreach ( var material in materials )
				mesh.AddFaceGroup( material?.Name ?? "dev/helper/testgrid.vmat" );

			mesh.AddFaceGroup( "materials/dev/reflectivity_30.vmat" );
			var invalidGroupIndex = materials.Length;

			var verticesRemap = new Dictionary<int, int>();
			var vertexHandles = polygonMesh.VertexHandles.ToArray();
			for ( var i = 0; i < vertexHandles.Length; i++ )
				verticesRemap.Add( vertexHandles[i].Index, i );

			var positions = vertexHandles.Select( x => polygonMesh.Transform.PointToWorld( polygonMesh.GetVertexPosition( x ) ) )
				.ToArray();

			fixed ( Vector3* pPositions = &positions[0] )
				mesh.SetPositions( (IntPtr)pPositions, positions.Length );

			foreach ( var hFace in polygonMesh.FaceHandles )
			{
				var groupIndex = polygonMesh.GetFaceMaterialIndex( hFace );
				var indices = polygonMesh.GetFaceVertices( hFace )
					.Select( x => verticesRemap[x.Index] )
					.ToArray();

				fixed ( int* pIndices = &indices[0] )
					mesh.AddFace( groupIndex >= 0 ? groupIndex : invalidGroupIndex, (IntPtr)pIndices, indices.Length );
			}

			var uvs = polygonMesh.GetFaceVertexTexCoords().ToArray();
			var normals = polygonMesh.GetFaceVertexNormals().ToArray();

			fixed ( Vector3* pNormals = &normals[0] )
				mesh.SetNormals( (IntPtr)pNormals, normals.Length );

			fixed ( Vector2* pUvs = &uvs[0] )
				mesh.SetTexCoords( (IntPtr)pUvs, uvs.Length );
		}

		var meshes_span = CollectionsMarshal.AsSpan( meshes );
		fixed ( CModelMesh* ptr = meshes_span )
		{
			var success = NativeEngine.ModelDoc.CreateModelFromMeshes( targetAbsolutePath, (IntPtr)ptr, meshes.Count );
			foreach ( var mesh in meshes )
				mesh.DeleteThis();

			if ( !success )
				return null;
		}

		var asset = AssetSystem.RegisterFile( targetAbsolutePath );
		if ( asset is null )
			return null;

		asset.Compile( true );

		return asset;
	}

	/// <summary>
	/// Create a vmdl file from mesh components. Will return non null if the asset was created successfully.
	/// The model's origin will be placed at the first mesh component's position.
	/// </summary>
	public static unsafe Asset CreateModelFromMeshComponents( MeshComponent[] meshComponents, string targetAbsolutePath )
	{
		if ( meshComponents is null || meshComponents.Length == 0 )
			return null;

		if ( string.IsNullOrWhiteSpace( targetAbsolutePath ) )
			return null;

		if ( !g_pToolFramework2.InitEngineTool( "modeldoc_editor" ) )
			return null;

		if ( !meshComponents[0].IsValid() )
			return null;

		// Use first mesh's world position as the model origin
		var origin = meshComponents[0].WorldPosition;

		var meshes = new List<CModelMesh>();
		var collisionTypes = new List<int>();

		foreach ( var meshComponent in meshComponents )
		{
			if ( !meshComponent.IsValid() )
				continue;

			var polygonMesh = meshComponent.Mesh;
			if ( polygonMesh is null )
				continue;

			var vertices = polygonMesh.VertexHandles.ToArray();
			if ( vertices.Length == 0 )
				continue;

			var mesh = CModelMesh.Create();
			meshes.Add( mesh );

			// Map collision type: None = 0, Mesh = 1, Hull = 2
			collisionTypes.Add( meshComponent.Collision switch
			{
				MeshComponent.CollisionType.None => 0,
				MeshComponent.CollisionType.Mesh => 1,
				MeshComponent.CollisionType.Hull => 2,
				_ => 1
			} );
			mesh.AddVertices( vertices.Length );

			var materials = polygonMesh.Materials.ToArray();
			foreach ( var material in materials )
				mesh.AddFaceGroup( material?.Name ?? "dev/helper/testgrid.vmat" );

			mesh.AddFaceGroup( "materials/dev/reflectivity_30.vmat" );
			var invalidGroupIndex = materials.Length;

			var verticesRemap = new Dictionary<int, int>();
			var vertexHandles = polygonMesh.VertexHandles.ToArray();
			for ( var i = 0; i < vertexHandles.Length; i++ )
				verticesRemap.Add( vertexHandles[i].Index, i );

			// Transform vertices to world space, then offset by origin to make the model origin-relative
			var positions = vertexHandles
				.Select( x => polygonMesh.Transform.PointToWorld( polygonMesh.GetVertexPosition( x ) ) - origin )
				.ToArray();

			fixed ( Vector3* pPositions = &positions[0] )
				mesh.SetPositions( (IntPtr)pPositions, positions.Length );

			foreach ( var hFace in polygonMesh.FaceHandles )
			{
				var groupIndex = polygonMesh.GetFaceMaterialIndex( hFace );
				var indices = polygonMesh.GetFaceVertices( hFace )
					.Select( x => verticesRemap[x.Index] )
					.ToArray();

				fixed ( int* pIndices = &indices[0] )
					mesh.AddFace( groupIndex >= 0 ? groupIndex : invalidGroupIndex, (IntPtr)pIndices, indices.Length );
			}

			var uvs = polygonMesh.GetFaceVertexTexCoords().ToArray();
			var normals = polygonMesh.GetFaceVertexNormals().ToArray();

			fixed ( Vector3* pNormals = &normals[0] )
				mesh.SetNormals( (IntPtr)pNormals, normals.Length );

			fixed ( Vector2* pUvs = &uvs[0] )
				mesh.SetTexCoords( (IntPtr)pUvs, uvs.Length );
		}

		if ( meshes.Count == 0 )
			return null;

		var meshes_span = CollectionsMarshal.AsSpan( meshes );
		var collisionTypes_span = CollectionsMarshal.AsSpan( collisionTypes );
		fixed ( CModelMesh* ptr = meshes_span )
		fixed ( int* pCollisionTypes = collisionTypes_span )
		{
			var success = NativeEngine.ModelDoc.CreateModelFromMeshesWithCollision( targetAbsolutePath, (IntPtr)ptr, (IntPtr)pCollisionTypes, meshes.Count );
			foreach ( var mesh in meshes )
				mesh.DeleteThis();

			if ( !success )
				return null;
		}

		var asset = AssetSystem.RegisterFile( targetAbsolutePath );
		if ( asset is null )
			return null;

		asset.Compile( true );

		return asset;
	}
}
