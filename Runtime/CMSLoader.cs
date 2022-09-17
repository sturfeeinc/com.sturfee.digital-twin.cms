using Newtonsoft.Json;
using Sturfee.XRCS;
using Sturfee.XRCS.Config;
using SturfeeVPS.Core;
using SturfeeVPS.SDK;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using UnityEngine;
using UnityGLTF;
using UnityGLTF.Loader;

namespace Sturfee.DigitalTwin.CMS
{
    public delegate void XrAssetLoadEvent(float progress, int _errorCount);
    public delegate void XrAssetErrorEvent(string message);

    public class CMSLoader : SimpleSingleton<CMSLoader>
    {        
        /// <summary>
        /// Checks if assets needed for this space is already downloaded and saved in local cache
        /// </summary>
        /// <param name="space"></param>
        /// <returns></returns>
        public async Task<bool> AvailableInCache(XrSceneData space)
        {
            try
            {
                var cmsProvider = IOC.Resolve<ICMSProvider>();
                var assets = await cmsProvider.GetProjectAssets(space);

                if (!assets.Any())
                {
                    MyLogger.Log($" CMSLoader :: Space {space.Name} id {space.Id} has no assets. Nothing to look in cache");
                }

                foreach (var asset in assets)
                {
                    if (asset.Type == XrAssetDataType.AssetTemplate)
                        continue;

                    string path = cmsProvider.GetSavePath(space, asset);
                    if (!Directory.Exists(path))
                        return false;
                }

                MyLogger.Log($" CMSLoader :: All assets for space {space.Id} available in cache ");

                return true;
            }
            catch (Exception ex)
            {
                MyLogger.LogException(ex);
                throw ex;
            }
        }

        /// <summary>
        /// Download assets for this space
        /// </summary>
        /// <param name="space"></param>
        /// <param name="progress"></param>
        /// <returns></returns>
        public async Task DoawnloadAssets(XrSceneData space, Action<float> progress = null)
        {
            try
            {
                var cmsProvider = IOC.Resolve<ICMSProvider>();
                var assets = await cmsProvider.GetProjectAssets(space);

                for (int i = 0; i < assets.Count; i++)
                {
                    if (assets[i].Type != XrAssetDataType.AssetTemplate)
                    {
                        await cmsProvider.DownloadAsset(space, assets[i]);
                    }
                    progress?.Invoke((float)(i + 1) / assets.Count);
                    MyLogger.Log($" CMSLoader :: Asset download progress : {(float)(i + 1) / assets.Count}");
                }

                progress?.Invoke(1);

                MyLogger.Log($" CMSLoader :: DONE downloading {assets.Count} assets for space {space} id { space.Id}");
            }
            catch (Exception ex)
            {
                MyLogger.LogException(ex);
                throw ex;
            }
        }

        /// <summary>
        /// Loads assets from local cache int tthe scene. If cache does not exist then downloads and loads into the scene
        /// </summary>
        /// <param name="space"></param>
        /// <param name="onXrAssetLoadProgress"></param>
        /// <param name="onXrSceneLoadProgress"></param>
        /// <param name="onXrSceneLoadError"></param>
        /// <returns></returns>
        public async Task LoadAssets(XrSceneData space, XrAssetLoadEvent onXrAssetLoadProgress = null, XrAssetLoadEvent onXrSceneLoadProgress = null, XrAssetErrorEvent onXrSceneLoadError = null)
        {
            try
            {
                var cmsProvider = IOC.Resolve<ICMSProvider>();
                var project = await cmsProvider.GetXrProject(space);
                var assets = await cmsProvider.GetProjectAssets(space);
              
                await LoadAssetsAsync(space,project, assets, onXrAssetLoadProgress);
                await LoadSceneAsync(space, space.SceneAssets, onXrSceneLoadProgress);
                
            }
            catch (Exception ex)
            {
                MyLogger.LogException(ex);
                onXrSceneLoadError?.Invoke(ex.Message);
                throw ex;
            }
        }

        private async Task LoadAssetsAsync(XrSceneData space, XrProjectData project, List<XrProjectAssetData> projectAssets, XrAssetLoadEvent onXrAssetLoadProgress = null)
        {
            var validProjectAssetIds = new List<Guid>();
            var currentProject = project;
            var prefabCount = 0;
            var expectedPrefabCount = projectAssets.Count;

            foreach (var asset in projectAssets)
            {
                MyLogger.Log($"CMSLoader :: Trying to load Asset with ID={asset.Id}");
                //var dataFilePath = $"{Application.persistentDataPath}/{XrConstants.LOCAL_ASSETS_PATH}/{assetId}/asset";
                //var dataFilePath = $"{Application.persistentDataPath}/{XrConstants.LOCAL_PROJECTS_PATH}/{currentProject.Id}/Assets/{asset.Id}/asset";
                var dataFilePath = $"{Application.persistentDataPath}/{DtConstants.LOCAL_SPACES_PATH}/{space.Id}/Assets/{asset.Id}";

                if (!Directory.Exists(dataFilePath) && (asset.Type == XrAssetDataType.Zip || asset.Type == XrAssetDataType.AssetBundle))
                {
                    MyLogger.Log($"CMSLoader ::   Downloading Asset with ID={asset.Id} ...");
                    // download the asset first                    
                    var cmsProvider = IOC.Resolve<ICMSProvider>();
                    await cmsProvider.DownloadAsset(space, asset);
                }

                try
                {
                    // GLTF => load meshes
                    if (asset.Type == XrAssetDataType.Zip)
                    {
                        //var meshFiles = Directory.GetFiles($"{Application.persistentDataPath}/{XrConstants.LOCAL_ASSETS_PATH}/{assetId}", "mesh.*");
                        if (!Directory.Exists($"{Application.persistentDataPath}/{XrConstants.LOCAL_PROJECTS_PATH}/{currentProject.Id}/Assets/{asset.Id}"))
                        {
                            Debug.LogWarning($"ProjectLoader ::      Directory NOT found for Asset with ID={asset.Id}");
                            MyLogger.LogError($"Asset could not be loaded (NOT FOUND): {asset.Name}");

                            var dummyPrefab = new GameObject(asset.Name);
                            LayerUtils.SetLayerRecursive(dummyPrefab, LayerMask.NameToLayer($"{XrLayers.XrAssetPrefab}"));

                            asset.Status = XrAssetStatus.Error;
                            asset.StatusMessage = $"Asset data missing. Please re-import.";

                            // set up prefab for drag-drop
                            var prefab = dummyPrefab.AddComponent<XrAssetPrefab>();
                            prefab.SetData(asset);

                            prefab.gameObject.SetActive(false);

                            prefabCount++;
                            continue;
                        }
                        var meshFiles = Directory.GetFiles(
                            Path.Combine($"{Application.persistentDataPath}", $"{XrConstants.LOCAL_PROJECTS_PATH}", $"{currentProject.Id}", "Assets", $"{asset.Id}"),
                            "mesh.gltf");
                        if (meshFiles.Length < 1)
                        {
                            // ignore if no mesh file exists
                            // TODO: remove unused asset from project
                            Debug.LogWarning($"CMSLoader ::      No mesh found for Asset with ID={asset.Id}");
                            prefabCount++;
                            continue;
                        }

                        // mesh
                        var meshType = Path.GetExtension(meshFiles[0]).Replace(".", "");
                        MyLogger.Log($"CMSLoader ::   Trying to load MESH with type={meshType.ToUpper()} => {meshFiles[0]}");
                        await LoadMeshAsync(meshFiles[0], meshType, asset);

                    }

                    // Asset Bundle => load prefabs
                    if (asset.Type == XrAssetDataType.AssetBundle)
                    {
                        var bundles = Directory.GetFiles($"{Application.persistentDataPath}/{XrConstants.LOCAL_PROJECTS_PATH}/{currentProject.Id}/Assets/{asset.Id}", "*.assetbundle");
                        if (bundles.Length < 1)
                        {
                            // ignore if no bundle file exists
                            // TODO: remove unused asset from project
                            Debug.LogWarning($"     No mesh found for Asset with ID={asset.Id}");
                            prefabCount++;
                            continue;
                        }

                        var bundleFilePath = bundles[0];
                        MyLogger.Log($"  Trying to load ASSET BUNDLE from {bundleFilePath}");
                        try
                        {
                            await LoadBundledAsset(bundleFilePath, asset);
                        }
                        catch (Exception ex)
                        {
                            MyLogger.LogError($"ERROR :: Could not load asset bundle from {bundleFilePath}");
                            throw ex;
                        }
                    }

                    if (asset.Type == XrAssetDataType.AssetTemplate)
                    {
                        MyLogger.Log($"CMSLoader :: load asset template (type={asset.TemplateType})");

                        var templateOption = TemplateAssetManager.CurrentInstance.Prefabs.FirstOrDefault(x => x.Type == asset.TemplateType);
                        if (templateOption != null)
                        {
                            var newProjectAsset = Instantiate(templateOption.Prefab);

                            LayerUtils.SetLayerRecursive(newProjectAsset, LayerMask.NameToLayer($"{XrLayers.XrAssetPrefab}"));
                            newProjectAsset.name = asset.Name;

                            // set up prefab for drag-drop
                            var pAsset = newProjectAsset.AddComponent<XrAssetPrefab>();
                            pAsset.SetData(asset);

                            if (asset.TemplateType == TemplateAssetType.Image)
                            {
                                var imageTemplateAsset = newProjectAsset.GetComponent<ImageTemplateAsset>();
                                imageTemplateAsset.AssetType = XrAssetType.ProjectAsset;

                                // get the data
                                var templateData = JsonConvert.DeserializeObject<ImageTemplateAssetData>(asset.TemplateData);
                                imageTemplateAsset.Data = templateData;

                                Guid imageId;
                                if (Guid.TryParse(templateData.ImageId, out imageId))
                                {
                                    //var thumbnailProvider = IOC.Resolve<IThumbnailProvider>();
                                    //var task = thumbnailProvider.GetThumbnail(imageId);
                                    //yield return new WaitUntil(() => task.IsCompleted);
                                    //yield return new WaitForEndOfFrame();
                                    //var texture = task.Result as Texture2D;

                                    LoadImageAsync(imageId, ImageFileType.png, (texture) =>
                                    {
                                        if (texture != null)
                                        {
                                            // resize texture
                                            bool resized;
                                            texture = ImageUtils.TryResizeImage(texture, 512, 512, out resized);

                                            //compress texture before loading into scene
                                            texture.Compress(false);

                                            imageTemplateAsset.SetData(texture, $"{imageId}", templateData.Caption);

                                            imageTemplateAsset.UpdateSceneAssetLinks(texture);
                                        }
                                        else
                                        {
                                            imageTemplateAsset.Image.sprite = null;
                                        }
                                    });
                                }
                            }

                            if (asset.TemplateType == TemplateAssetType.Billboard)
                            {
                                var billboardTemplateAsset = newProjectAsset.GetComponent<BillboardTemplateAsset>();
                                billboardTemplateAsset.AssetType = XrAssetType.ProjectAsset;

                                // get the data
                                var templateData = JsonConvert.DeserializeObject<BillboardTemplateAssetData>(asset.TemplateData);

                                MyLogger.Log($"CMSLoader :: Load Billboard Image with ID = {templateData.ImageId}");

                                billboardTemplateAsset.SetData(
                                    null,
                                    templateData);

                                Guid imageId;
                                if (Guid.TryParse(templateData.ImageId, out imageId))
                                {
                                    MyLogger.Log($"CMSLoader :: LOADING Billboard Thumbnail with ID = {templateData.ImageId}");
                                    LoadImageAsync(imageId, ImageFileType.png, (texture) =>
                                    {
                                        if (texture != null)
                                        {
                                            MyLogger.Log($"CMSLoader :: LOADED Billboard Image with ID = {templateData.ImageId}");

                                            billboardTemplateAsset.SetData(
                                                texture,
                                                templateData);

                                            billboardTemplateAsset.UpdateSceneAssetLinks();
                                        }
                                        else
                                        {
                                            MyLogger.LogError($"CMSLoader :: ERROR LOADING Billboard Image with ID = {templateData.ImageId}");
                                            //billboardTemplateAsset.Background.sprite = null;
                                            billboardTemplateAsset.Background.texture = null;
                                            billboardTemplateAsset.SetData(null, templateData);
                                            billboardTemplateAsset.ImageLoader.SetActive(false);
                                        }
                                    });
                                }
                                else
                                {
                                    billboardTemplateAsset.SetData(null, templateData);
                                    billboardTemplateAsset.ImageLoader.SetActive(false);
                                }
                            }

                            if (asset.TemplateType == TemplateAssetType.SpawnPoint)
                            {
                                var spawnPointTemplateAsset = newProjectAsset.GetComponent<SpawnPointTemplateAsset>();
                                spawnPointTemplateAsset.AssetType = XrAssetType.ProjectAsset;

                                MyLogger.Log($"CMSLoader :: Load SpawnPoint");
                            }

                            //if (asset.TemplateType == TemplateAssetType.WebView)
                            //{
                            //    var webviewTemplateAsset = newProjectAsset.GetComponent<WebViewTemplateAsset>();
                            //    webviewTemplateAsset.AssetType = XrAssetType.ProjectAsset;
                            //    webviewTemplateAsset.enabled = true;

                            //    // get the data
                            //    var templateData = JsonConvert.DeserializeObject<WebViewTemplateAssetData>(asset.TemplateData);

                            //    MyLogger.Log($"ProjectLoader :: Load WebView Asset with URL = {templateData.Url}");

                            //    webviewTemplateAsset.SetData(templateData);
                            //}


                            pAsset.gameObject.SetActive(false);
                        }

                        prefabCount++;
                    }
                    
                    validProjectAssetIds.Add(asset.Id);
                }
                catch (Exception ex)
                {
                    prefabCount++;
                    MyLogger.LogError(ex.Message);
                }

                if (prefabCount >= expectedPrefabCount)
                {
                    MyLogger.Log($"ProjectLoader :: All Prefabs have been loaded!");                    
                    if (prefabCount == expectedPrefabCount) onXrAssetLoadProgress?.Invoke(1, 0);
                }
                else
                {
                    onXrAssetLoadProgress?.Invoke((float)prefabCount / (float)expectedPrefabCount, 0);
                }
            }

            currentProject.ProjectAssetIds = validProjectAssetIds;
        }

        private async Task LoadSceneAsync(XrSceneData xrScene, List<XrSceneAssetData> sceneAssets, XrAssetLoadEvent onXrSceneLoadProgress = null)
        {
            MyLogger.Log($"CMSLoader :: Loading Scene: {JsonConvert.SerializeObject(xrScene)}");

            var prefabs = FindObjectsOfType<XrAssetPrefab>(true)
                .ToList()
                .Distinct()
                .ToDictionary(x => x.XrProjectAssetData.Id);

            MyLogger.Log($"CMSLoader :: Loading Scene: scene asset count = {xrScene.SceneAssets.Count}");

            //var task = _projectProvider.GetSceneAssets(xrScene.Id);
            //yield return new WaitUntil(() => task.IsCompleted);
            //var sceneAssets = task.Result;

            if (!sceneAssets.Any())
            {
                sceneAssets = await IOC.Resolve<IProjectProvider>().GetSceneAssets(xrScene.Id);
            }


            MyLogger.Log($"CMSLoader :: Fetched scene assets count = {sceneAssets.Count}");

            if (sceneAssets.Count < 1)
            {
                onXrSceneLoadProgress?.Invoke(1, 0);
            }

            var sceneAssetLoadCount = 0;
            MyLogger.Log($"CMSLoader :: Scene Load Progress: {(float)sceneAssetLoadCount / (float)sceneAssets.Count}");
            foreach (var sceneAsset in sceneAssets) // xrScene.SceneAssetData)
            {
                if (prefabs.ContainsKey(sceneAsset.ProjectAssetId))
                {
                    var prefab = prefabs[sceneAsset.ProjectAssetId].gameObject;

                    // set up asset data
                    var newXrAsset = Instantiate(prefab);
                    newXrAsset.SetActive(true);

                    LayerUtils.SetLayerRecursive(newXrAsset, LayerMask.NameToLayer($"{XrLayers.XrAssets}"));

                    // remove prefab data from new instance
                    var prefabData = newXrAsset.GetComponent<XrAssetPrefab>();
                    Destroy(prefabData);

                    newXrAsset.transform.position = -1000 * Vector3.up;

                    //// expose to editor
                    //ExposePrefabInstance(newXrAsset);

                    // set up scene asset data
                    var newSceneAsset = newXrAsset.AddComponent<XrSceneAsset>();
                    newSceneAsset.XrSceneAssetData = sceneAsset;
                    newXrAsset.gameObject.name = sceneAsset.Name;

                    //XrRefId refIdData = newXrAsset.GetComponent<XrRefId>();
                    //if (refIdData == null)
                    //{
                    //    refIdData = newXrAsset.AddComponent<XrRefId>();
                    //}
                    //refIdData.ReferenceId = sceneAsset.RefId;

                    // load the location data
                    // POSITION
                    var xrLocation = newXrAsset.GetComponent<XrGeoLocation>();
                    if (xrLocation == null)
                    {
                        xrLocation = newXrAsset.AddComponent<XrGeoLocation>();
                    }
                    xrLocation.GPS = new SturfeeVPS.Core.GeoLocation
                    {
                        Latitude = sceneAsset.Location.Latitude,
                        Longitude = sceneAsset.Location.Longitude,
                        Altitude = sceneAsset.Location.Altitude
                    };

                    // load in the transform values
                    // ROTATION
                    var xrRotation = sceneAsset.Transform.Orientation;
                    var rotation = new Quaternion(xrRotation.X, xrRotation.Y, xrRotation.Z, xrRotation.W);
                    newXrAsset.transform.rotation = rotation;

                    // SCALE
                    var xrScale = sceneAsset.Transform.Scale;
                    var scale = new Vector3(xrScale.X, xrScale.Y, xrScale.Z);
                    newXrAsset.transform.localScale = scale;

                    var billboardTemplateAsset = newXrAsset.GetComponent<BillboardTemplateAsset>();
                    if (billboardTemplateAsset != null)
                    {
                        billboardTemplateAsset.AssetType = XrAssetType.SceneAsset;
                    }
                    var spawnPointTemplateAsset = newXrAsset.GetComponent<SpawnPointTemplateAsset>();
                    if (spawnPointTemplateAsset != null)
                    {
                        spawnPointTemplateAsset.AssetType = XrAssetType.SceneAsset;
                    }
                    //var webviewTemplateAsset = newXrAsset.GetComponent<WebViewTemplateAsset>();
                    //if (webviewTemplateAsset != null)
                    //{
                    //    webviewTemplateAsset.AssetType = XrAssetType.SceneAsset;
                    //    webviewTemplateAsset.enabled = true;
                    //    webviewTemplateAsset.InitWebView();
                    //}

                    MyLogger.Log($"CMSLoader :: Scene Asset Added: {JsonConvert.SerializeObject(sceneAsset)}");
                }

                sceneAssetLoadCount++;

                //yield return null;

                MyLogger.Log($"CMSLoader :: Scene Load Progress: {(float)sceneAssetLoadCount / (float)sceneAssets.Count}");
                onXrSceneLoadProgress?.Invoke((float)sceneAssetLoadCount / (float)sceneAssets.Count, 0);
            }

            //EditorManager.CurrentInstance.SetAssetLoadingStatus(true);

            //OnXrSceneLoadProgress?.Invoke(1, 0);
        }

        private async Task LoadBundledAsset(string bundleFilePath, XrProjectAssetData xrAssetData)
        {
            // try to get the file for the current platform
            string filePrepend = null;
            var nameParts = bundleFilePath.Split('.');
            if (nameParts.Length > 2)
            {
                filePrepend = string.Join(".", nameParts.Take(nameParts.Length - 2));
            }
            else if (nameParts.Length == 2)
            {
                filePrepend = string.Join(".", nameParts.Take(nameParts.Length - 1)); // deal with backwards compat (i.e. test-bundle.assetbundle)
            }
            else
            {
                Debug.LogError($"Bundle has invalid naming scheme: {bundleFilePath}");
                MyLogger.LogError($"Error Loading Asset ({xrAssetData.Name})");
                return;
            }

            var myPlatformFile = AssetBundlePlatformHelper.GetBundleFileForCurrentPlatform(filePrepend);

            if (!File.Exists(myPlatformFile))
            {
                Debug.LogError($"Bundle does not exist: {bundleFilePath}");
                MyLogger.LogError($"Error Loading Asset ({xrAssetData.Name})");
                return;
            }

            var url = "file:///" + myPlatformFile;
            var request = UnityEngine.Networking.UnityWebRequestAssetBundle.GetAssetBundle(url, 0);
            await request.SendWebRequest();

            AssetBundle bundle;
            try
            {
                MyLogger.Log($"Getting Asset Bundle Content...");
                bundle = UnityEngine.Networking.DownloadHandlerAssetBundle.GetContent(request);
                //bundle = AssetBundle.LoadFromFile(bundleFilePath);
            }
            catch (Exception e)
            {
                if (e.Message.Contains("build target that is not compatible with this platform"))
                {
                    Debug.LogError($"  ERROR: Asset Bundle for different platform");
                }
                throw e;
            }

            if (bundle == null)
            {
                Debug.LogError($"ERROR :: Could not load Unity Asset Bundle");
                MyLogger.LogError($"Error Loading Asset ({xrAssetData.Name})");
                return;
            }

            AssetBundleLoader.CurrentInstance.AddLoadedBundle(bundle);

            MyLogger.Log($"  Trying to load ASSET from BUNDLE with prefabUrl={xrAssetData.DataUrl}");
            var instance = Instantiate(bundle.LoadAsset<GameObject>(xrAssetData.DataUrl));
            if (instance == null)
            {
                MyLogger.LogError($"ERROR :: Could not load asset {xrAssetData.Name} from Unity Asset Bundle");
                return;
            }

            var rtData = instance.AddComponent<RuntimeAssetBundleData>();
            rtData.Name = Path.GetFileNameWithoutExtension(instance.name);
            rtData.Url = xrAssetData.DataUrl;

            LayerUtils.SetLayerRecursive(instance, LayerMask.NameToLayer($"{XrLayers.XrAssetPrefab}"));
            instance.name = xrAssetData.Name;

            // set up prefab for drag-drop
            var prefab = instance.AddComponent<XrAssetPrefab>();
            prefab.SetData(xrAssetData);

            XrRefId refIdData = instance.GetComponent<XrRefId>();
            if (refIdData == null)
            {
                refIdData = instance.AddComponent<XrRefId>();
            }
            refIdData.ReferenceId = xrAssetData.RefId;

            prefab.gameObject.SetActive(false);

            bundle.Unload(false);
            AssetBundleLoader.CurrentInstance.RemoveUnloadedBundle(bundle);
        }

        private async Task LoadMeshAsync(string filePath, string meshType, XrProjectAssetData xrAssetData)
        {
            var _importOptions = new ImportOptions
            {
                DataLoader = new FileLoader(Path.GetDirectoryName(filePath)),
                AsyncCoroutineHelper = gameObject.AddComponent<AsyncCoroutineHelper>(),
            };

            MyLogger.Log($"ProjectLoader :: Khronos => Loading file = {filePath}");

            try
            {
                var filename = Path.GetFileNameWithoutExtension(filePath);
                var obj = new GameObject($"{xrAssetData.Name}");

                var importer = new GLTFSceneImporter(filePath, _importOptions);

                importer.Collider = GLTFSceneImporter.ColliderType.Mesh;
                importer.SceneParent = obj.transform;

                await importer.LoadSceneAsync(-1, true, (go, err) => { OnFinishAsync(filePath, xrAssetData, go, err); });
            }
            catch (Exception ex)
            {
                Debug.LogError($"ProjectLoader :: ERROR LOADING GLTF");
                MyLogger.LogError(ex);
                //throw;
            }
        }

        private void OnFinishAsync(string filepath, XrProjectAssetData xrAssetData, GameObject result, ExceptionDispatchInfo info)
        {
            if (result == null) { Debug.LogError($"ProjectLoader :: No Mesh Found for Import ({filepath})\n{JsonConvert.SerializeObject(xrAssetData)}"); return; }

            MyLogger.Log("ProjectLoader :: Finished importing GLTF: " + result.name);

            //var filename = Path.GetFileNameWithoutExtension(filepath);
            //var obj = new GameObject($"{filename}");

            var newMesh = new GameObject(result.name);
            result.transform.parent = newMesh.transform;

            LayerUtils.SetLayerRecursive(newMesh, LayerMask.NameToLayer($"{XrLayers.XrAssetPrefab}"));
            newMesh.name = xrAssetData.Name;

            // set up prefab for drag-drop
            var prefab = newMesh.AddComponent<XrAssetPrefab>();
            prefab.SetData(xrAssetData);

            prefab.gameObject.SetActive(false);
        }
       
        private async void LoadImageAsync(Guid imageId, ImageFileType type, Action<Texture2D> _callback, bool resize = true, int maxWidth = 512, int maxHeight = 512, bool compress = true)
        {
            var thumbnailProvider = IOC.Resolve<IThumbnailProvider>();
            var texture = await thumbnailProvider.GetThumbnail(imageId, type) as Texture2D;

            //await Task.Delay(10000);

            if (texture != null)
            {
                if (resize)
                {
                    // resize texture
                    bool resized;
                    texture = ImageUtils.TryResizeImage(texture, maxWidth, maxHeight, out resized);
                }

                if (compress)
                {
                    // compress texture before loading into scene
                    texture.Compress(false);
                }
            }

            _callback?.Invoke(texture);
        }
    }
}
