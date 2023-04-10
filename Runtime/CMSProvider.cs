using Newtonsoft.Json;
using SturfeeVPS.Core.Constants;
using Sturfee.XRCS;
using Sturfee.XRCS.Config;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace Sturfee.DigitalTwin.CMS
{
    public interface ICMSProvider
    {
        Task<List<XrProjectAssetData>> GetProjectAssets(XrSceneData spaceData);
        Task DownloadAsset(XrSceneData space, XrProjectAssetData assetData);
        string GetSavePath(XrSceneData space, XrProjectAssetData assetData);


        Task<XrProjectData> GetXrProject(XrSceneData space);
        Task<List<XrProjectAssetData>> GetProjectAssetsForSpace(XrProjectData project, XrSceneData spaceData);
    }

    public class WebCMSProvider : ICMSProvider
    {
        private string _localSpacesPath;

        public WebCMSProvider()
        {
            _localSpacesPath = Path.Combine(Application.persistentDataPath, DtConstants.LOCAL_SPACES_PATH);
            if(!Directory.Exists(_localSpacesPath))  Directory.CreateDirectory(_localSpacesPath);
        }

        public async Task<List<XrProjectAssetData>> GetProjectAssets(XrSceneData spaceData)
        {
            try
            {
                // get project assets used in this scene
                var project = await GetXrProject(spaceData);
                var projectAssets = spaceData.SceneAssets.Select(x => x.ProjectAsset)
                    .GroupBy(x => x.Id).Select(x => x.First()).Distinct().ToList();
                MyLogger.Log($"CMSProvider :: Loading Project Assets: {JsonConvert.SerializeObject(projectAssets)}");

                if (projectAssets == null) { projectAssets = new List<XrProjectAssetData>(); }


                if (!projectAssets.Any() || project.ProjectAssetIds == null)
                {
                    MyLogger.Log($"CMSProvider :: No Assets found for project {project.Name}");                    
                    return projectAssets;
                }

                var sceneAssets = spaceData.SceneAssets;
                var usedProjectAssetIds = sceneAssets.Select(x => x.ProjectAssetId);
                var usedProjectAssets = projectAssets.Where(x => usedProjectAssetIds.Contains(x.Id)).ToList();

                MyLogger.Log($"CMSProvider :: Loading {usedProjectAssets.Count} Assets for project {project.Name}\n project = {JsonConvert.SerializeObject(project)}\n Ids to load = {JsonConvert.SerializeObject(projectAssets.Select(x => x.Id))}");

                return usedProjectAssets;
            }
            catch (Exception ex)
            {
                MyLogger.LogError(ex.Message);
                throw ex;
            }
        }

        public async Task DownloadAsset(XrSceneData space, XrProjectAssetData assetData)
        {
            try
            {
                var projectProvider = IOC.Resolve<IProjectProvider>();
                if (assetData.ProjectId == null)
                {
                    MyLogger.LogError($"CMSProvider ::  No projectId for asset {assetData.Name}");
                    throw new Exception(" AssetData has no reference to project Id");
                }

                await projectProvider.DownloadProjectAssets(assetData.ProjectId, new List<XrProjectAssetData> { assetData });

                // Copy from XRCS file system to Spaces file system
                string xrcsPath = Path.Combine(Application.persistentDataPath, XrConstants.LOCAL_PROJECTS_PATH, $"{assetData.ProjectId}", "Assets", $"{assetData.Id}");
                string spacesPath = Path.Combine(_localSpacesPath, $"{space.Id}", "Assets", $"{assetData.Id}");                                
                if(!Directory.Exists(spacesPath))      Directory.CreateDirectory(spacesPath);

                MyLogger.Log($"Copying asset {assetData.Name} from {xrcsPath} to {spacesPath}");
                CopyFilesRecursively(xrcsPath, spacesPath);
            }
            catch(Exception ex)
            {
                MyLogger.LogError(ex.Message);
                throw ex;
            }
        }

        public string GetSavePath(XrSceneData space, XrProjectAssetData assetData)
        {
            return Path.Combine(Application.persistentDataPath, DtConstants.LOCAL_SPACES_PATH, $"{space.Id}", "Assets", $"{assetData.Id}");            
        }


        public async Task<List<XrProjectAssetData>> GetProjectAssetsForSpace(XrProjectData project, XrSceneData space)
        {            
            try
            {
                // get project assets matching this scene
                var projectAssets = space.SceneAssets.Select(x => x.ProjectAsset)
                    .GroupBy(x => x.Id).Select(x => x.First()).Distinct().ToList();
                MyLogger.Log($"CMSProvider :: Loading Project Assets: {JsonConvert.SerializeObject(projectAssets)}");

                if (projectAssets == null) { projectAssets = new List<XrProjectAssetData>(); }

                //_expectedPrefabCount = projectAssets.Count;

                if (!projectAssets.Any()) //xrProject.ProjectAssetIds == null)
                {
                    MyLogger.Log($"CMSProvider :: No Assets found for project {project.Name}");
                    //OnXrAssetLoadProgress?.Invoke(1, 0);
                    //OnXrSceneLoadProgress?.Invoke(1, 0);
                    return projectAssets;
                }

                var sceneAssets = space.SceneAssets;

                var usedProjectAssetIds = sceneAssets.Select(x => x.ProjectAssetId);
                var usedProjectAssets = projectAssets.Where(x => usedProjectAssetIds.Contains(x.Id)).ToList();
                //_expectedPrefabCount = usedProjectAssets.Count;

                MyLogger.Log($"CMSProvider :: Loading {usedProjectAssets.Count} Assets for project {project.Name}\n project = {JsonConvert.SerializeObject(project)}\n Ids to load = {JsonConvert.SerializeObject(projectAssets.Select(x => x.Id))}");

                return usedProjectAssets;
            }
            catch (Exception ex)
            {
                MyLogger.LogError(ex.Message);
                throw ex;
            }
        }

        public async Task<XrProjectData> GetXrProject(XrSceneData space)
        {
            var projectProvider = IOC.Resolve<IProjectProvider>();
            var project = space.Project;
            if (project == null || (project != null && project.Id != space.ProjectId))
            {
                MyLogger.Log($"CMSProvider :: Downloading {space.ProjectId} ");
                project = await projectProvider.GetPublicXrProject(space.ProjectId);
            }
            MyLogger.Log($"CMSProvider :: Loaded Project: {JsonConvert.SerializeObject(project)}");

            if (project == null)
            {
                Debug.LogError($"CMSProvider :: Layer can't be loaded. Make sure it is public if loading from non-owner.");
                //OnXrSceneLoadError?.Invoke("Layer can't be loaded. Make sure it is not private.");
                throw new Exception("Layer can't be loaded. Make sure it is not private.");
            }

            return project;
        }

        private void CopyFilesRecursively(string sourcePath, string targetPath)
        {
            //Now Create all of the directories
            foreach (string dirPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(sourcePath, targetPath));
            }

            //Copy all the files & Replaces any files with the same name
            foreach (string newPath in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories))
            {
                File.Copy(newPath, newPath.Replace(sourcePath, targetPath), true);
            }
        }
    }
}
