using System;
using System.Runtime.InteropServices;
using NativeEngine;

namespace Editor;

public static partial class EditorUtility
{
	/// <summary>
	/// Create a vmdl file from a mesh. Will return non null if the asset was created successfully
	/// </summary>
	public static unsafe Asset CreateModelFromMeshFile( Asset meshFile, string targetAbsolutePath = null )
	{
		var modelFilename = targetAbsolutePath ?? System.IO.Path.ChangeExtension( meshFile.GetSourceFile( true ), ".vmdl" );

		if ( System.IO.File.Exists( modelFilename ) )
			return null;

		// In the future we could just init all tools upfront
		if ( !g_pToolFramework2.InitEngineTool( "modeldoc_editor" ) )
			return null;

		var document = CModelDoc.Create();
		g_pModelDocUtils.InitFromMesh( document, meshFile.Path );
		document.SaveToFile( modelFilename );
		document.DeleteThis();

		var asset = AssetSystem.RegisterFile( modelFilename );
		if ( asset is null )
			return null;

		asset.Compile( true );

		return asset;
	}

	/// <summary>
	/// Create vmdl files for each submesh and combine them in a prefab. Will return non null if the asset was created successfully
	/// </summary>
	public static unsafe Asset CreatePrefabAndModelsFromMeshFile( Asset meshFile, string targetAbsolutePath = null )
	{
		Log.Info( "Extracting mesh to prefab..." );
		var sourceFile = meshFile.GetSourceFile( true );
		var prefabFilename = targetAbsolutePath ?? System.IO.Path.ChangeExtension( sourceFile, ".prefab" );
		if ( System.IO.File.Exists( prefabFilename ) )
			return null;

		var folderPath = System.IO.Path.Combine(
			System.IO.Path.GetDirectoryName( prefabFilename ) ?? string.Empty,
			System.IO.Path.GetFileNameWithoutExtension( prefabFilename )
		);

		Log.Info( "Creating directory at: " + folderPath );
		System.IO.Directory.CreateDirectory( folderPath );

		if ( !g_pToolFramework2.InitEngineTool( "modeldoc_editor" ) )
			return null;

		var previewModel = meshFile.GetPreviewModel();
		if ( previewModel is null || !previewModel.IsValid )
		{
			Log.Warning( $"Could not load preview model for mesh {meshFile.Path}." );
			return null;
		}

		var meshes = new List<(string ChildName, string MeshInput)>();
		var bodyPartCount = previewModel.native.GetNumBodyParts();

		if ( bodyPartCount > 0 )
		{
			for ( var i = 0; i < bodyPartCount; i++ )
			{
				var partName = previewModel.native.GetBodyPartName( i );
				if ( string.IsNullOrWhiteSpace( partName ) )
					partName = $"part_{i}";

				meshes.Add( (partName, $"{meshFile.Path}:{partName}") );
			}
		}
		else if ( previewModel.MeshCount > 0 )
		{
			for ( var i = 0; i < previewModel.MeshCount; i++ )
			{
				var meshName = previewModel.native.GetBodyPartMeshName( 0, i );
				if ( string.IsNullOrWhiteSpace( meshName ) )
					meshName = $"mesh_{i}";

				meshes.Add( (meshName, $"{meshFile.Path}:{meshName}") );
			}
		}
		else
		{
			meshes.Add( ("mesh", meshFile.Path) );
		}

		var createdAssets = new List<Asset>();

		foreach ( var (childName, meshInput) in meshes )
		{
			var safeName = MakeSafeFilename( childName );
			var exportModelPath = System.IO.Path.Combine( folderPath, safeName + ".vmdl" );

			if ( System.IO.File.Exists( exportModelPath ) )
			{
				Log.Warning( $"Skipping existing mesh output '{exportModelPath}'" );
				continue;
			}

			var success = NativeEngine.ModelDoc.CreateModelFromMeshFile( exportModelPath, meshInput, "materials/dev/reflectivity_30.vmat" );
			if ( !success )
			{
				Log.Warning( $"Failed to create child model '{meshInput}'" );
				continue;
			}

			var childAsset = AssetSystem.RegisterFile( exportModelPath );
			if ( childAsset is not null )
				childAsset.Compile( true );

			if ( childAsset is not null )
				createdAssets.Add( childAsset );
		}

		if ( createdAssets.Count == 0 )
		{
			Log.Warning( "No child models were created." );
			return null;
		}

		var prefabScene = new PrefabScene( true );
		prefabScene.Source = new PrefabFile();
		prefabScene.Source.RegisterWeakResourceId( prefabFilename );
		prefabScene.Source.Register( prefabFilename );

		GameObject root = null;
		using ( prefabScene.Push() )
		{
			root = new GameObject( prefabScene );
			root.Name = System.IO.Path.GetFileNameWithoutExtension( prefabFilename );

			foreach ( var childAsset in createdAssets )
			{
				if ( childAsset is null )
					continue;

				var childGo = new GameObject( prefabScene );
				childGo.Name = childAsset.Name;
				childGo.Parent = root;

				var renderer = childGo.Components.Create<ModelRenderer>();
				renderer.Model = Model.Load( childAsset.Path );
			}
		}

		if ( root is null )
			return null;

		EditorUtility.Prefabs.ConvertGameObjectToPrefab( root, prefabFilename );

		var prefabAsset = AssetSystem.RegisterFile( prefabFilename );
		if ( prefabAsset is not null )
			prefabAsset.Compile( true );

		return prefabAsset;
	}

	private static string MakeSafeFilename( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return "mesh";

		var invalidChars = System.IO.Path.GetInvalidFileNameChars();
		var safe = new string( name.Select( c => invalidChars.Contains( c ) ? '_' : c ).ToArray() );

		if ( string.IsNullOrWhiteSpace( safe ) )
			return "mesh";

		return safe;
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
