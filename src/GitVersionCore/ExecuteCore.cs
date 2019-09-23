using System;
using System.Linq;
using GitVersion.BuildServers;
using GitVersion.Configuration;
using GitVersion.OutputVariables;
using GitVersion.Cache;
using GitVersion.Common;
using GitVersion.Log;
using Environment = System.Environment;

namespace GitVersion
{
    public class ExecuteCore
    {
        private readonly IFileSystem fileSystem;
        private readonly IEnvironment environment;
        private readonly ILog log;
        private readonly ConfigFileLocator configFileLocator;
        private readonly GitVersionCache gitVersionCache;

        public ExecuteCore(IFileSystem fileSystem, IEnvironment environment, ILog log, ConfigFileLocator configFileLocator = null)
        {
            this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            this.environment = environment;
            this.log = log;
            this.configFileLocator = configFileLocator ?? ConfigFileLocator.Default;
            gitVersionCache = new GitVersionCache(fileSystem, log);
        }

        public VersionVariables ExecuteGitVersion(string targetUrl, string dynamicRepositoryLocation, Authentication authentication, string targetBranch, bool noFetch, string workingDirectory, string commitId, Config overrideConfig = null, bool noCache = false, bool noNormalize = false)
        {
            BuildServerList.Init(environment, log);

            // Normalize if we are running on build server
            var applicableBuildServers = BuildServerList.GetApplicableBuildServers(log);
            var buildServer = applicableBuildServers.FirstOrDefault();
            var normalizeGitDirectory = !noNormalize && buildServer != null;
            var fetch = noFetch || buildServer != null && buildServer.PreventFetch();
            var shouldCleanUpRemotes = buildServer != null && buildServer.ShouldCleanUpRemotes();
            var gitPreparer = new GitPreparer(log, targetUrl, dynamicRepositoryLocation, authentication, fetch, workingDirectory);

            gitPreparer.Initialise(normalizeGitDirectory, ResolveCurrentBranch(buildServer, targetBranch, !string.IsNullOrWhiteSpace(dynamicRepositoryLocation)), shouldCleanUpRemotes);
            var dotGitDirectory = gitPreparer.GetDotGitDirectory();
            var projectRoot = gitPreparer.GetProjectRootDirectory();

            // TODO Can't use this, it still needs work
            //var gitRepository = GitRepositoryFactory.CreateRepository(new RepositoryInfo
            //{
            //    Url = targetUrl,
            //    Branch = targetBranch,
            //    Authentication = new AuthenticationInfo
            //    {
            //        Username = authentication.Username,
            //        Password = authentication.Password
            //    },
            //    Directory = workingDirectory
            //});
            log.Info($"Project root is: {projectRoot}");
            log.Info($"DotGit directory is: {dotGitDirectory}");
            if (string.IsNullOrEmpty(dotGitDirectory) || string.IsNullOrEmpty(projectRoot))
            {
                // TODO Link to wiki article
                throw new Exception($"Failed to prepare or find the .git directory in path '{workingDirectory}'.");
            }

            var cacheKey = GitVersionCacheKeyFactory.Create(fileSystem, gitPreparer, overrideConfig, configFileLocator);
            var versionVariables = noCache ? default : gitVersionCache.LoadVersionVariablesFromDiskCache(gitPreparer, cacheKey);
            if (versionVariables == null)
            {
                versionVariables = ExecuteInternal(targetBranch, commitId, gitPreparer, buildServer, overrideConfig);

                if (!noCache)
                {
                    try
                    {
                      gitVersionCache.WriteVariablesToDiskCache(gitPreparer, cacheKey, versionVariables);
                    }
                    catch (AggregateException e)
                    {
                        log.Warning($"One or more exceptions during cache write:{Environment.NewLine}{e}");
                    }
                }
            }

            return versionVariables;
        }

        public bool TryGetVersion(string directory, out VersionVariables versionVariables, bool noFetch, Authentication authentication)
        {
            try
            {
                versionVariables = ExecuteGitVersion(null, null, authentication, null, noFetch, directory, null);
                return true;
            }
            catch (Exception ex)
            {
                log.Warning("Could not determine assembly version: " + ex);
                versionVariables = null;
                return false;
            }
        }

        string ResolveCurrentBranch(IBuildServer buildServer, string targetBranch, bool isDynamicRepository)
        {
            if (buildServer == null)
            {
                return targetBranch;
            }

            var currentBranch = buildServer.GetCurrentBranch(isDynamicRepository) ?? targetBranch;
            log.Info("Branch from build environment: " + currentBranch);

            return currentBranch;
        }

        VersionVariables ExecuteInternal(string targetBranch, string commitId, GitPreparer gitPreparer, IBuildServer buildServer, Config overrideConfig = null)
        {
            var versionFinder = new GitVersionFinder();
            var configuration = ConfigurationProvider.Provide(gitPreparer, fileSystem, overrideConfig: overrideConfig, configFileLocator: configFileLocator);

            return gitPreparer.WithRepository(repo =>
            {
                var gitVersionContext = new GitVersionContext(repo, targetBranch, configuration, commitId: commitId);
                var semanticVersion = versionFinder.FindVersion(gitVersionContext);

                return VariableProvider.GetVariablesFor(semanticVersion, gitVersionContext.Configuration, gitVersionContext.IsCurrentCommitTagged);
            });
        }
    }
}
