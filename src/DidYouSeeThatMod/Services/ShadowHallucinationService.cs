using System;
using System.Collections.Generic;
using BepInEx.Logging;
using DidYouSeeThatMod.Configuration;
using DidYouSeeThatMod.Internal;
using UnityEngine;
using UnityEngine.Rendering;

namespace DidYouSeeThatMod.Services
{
    /// <summary>
    /// Identifies the visual template used for a shadow apparition.
    /// </summary>
    public enum ShadowModelKind : byte
    {
        /// <summary>
        /// Uses the R.E.P.O. player avatar silhouette.
        /// </summary>
        Player = 0,

        /// <summary>
        /// Uses a spawned game entity (<see cref="EnemyParent"/>) silhouette.
        /// </summary>
        Entity = 1
    }

    /// <summary>
    /// Spawns and animates fleeting shadow hallucinations using visual mesh snapshots of players or game entities.
    /// </summary>
    public sealed class ShadowHallucinationService
    {
        private readonly ModConfig _config;
        private readonly ManualLogSource _logger;
        private Material? _shadowMaterialTemplate;

        /// <summary>
        /// Initializes a new instance of the <see cref="ShadowHallucinationService"/> class.
        /// </summary>
        /// <param name="config">Mod configuration instance.</param>
        /// <param name="logger">BepInEx logger instance.</param>
        public ShadowHallucinationService(ModConfig config, ManualLogSource logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Picks a random shadow model kind (50% Player, 50% Game Entity) and an entity index if applicable.
        /// </summary>
        /// <param name="entityIndex">Outputs the index in <see cref="EnemyDirector.enemiesSpawned"/> when an entity is chosen.</param>
        /// <returns>The chosen <see cref="ShadowModelKind"/>.</returns>
        public ShadowModelKind PickRandomModel(out int entityIndex)
        {
            entityIndex = -1;
            var enemyDirector = EnemyDirector.instance;
            var spawnedList = enemyDirector != null ? enemyDirector.enemiesSpawned : null;
            var hasEntities = spawnedList != null && spawnedList.Count > 0;

            if (hasEntities && UnityEngine.Random.value < 0.5f)
            {
                entityIndex = UnityEngine.Random.Range(0, spawnedList!.Count);
                return ShadowModelKind.Entity;
            }

            return ShadowModelKind.Player;
        }

        /// <summary>
        /// Calculates a distant visible trajectory (12.5m to 28m ahead) where the shadow either
        /// crosses a corridor in front of the player or stands static watching from the distance for ~2 seconds.
        /// </summary>
        /// <param name="localPlayer">The local player avatar.</param>
        /// <param name="startPos">Outputs the floor-snapped starting world position.</param>
        /// <param name="endPos">Outputs the floor-snapped ending world position (equal to <paramref name="startPos"/> when static).</param>
        /// <param name="duration">Outputs the duration in seconds (~2.0s).</param>
        /// <returns><see langword="true"/> if a valid distant spot was found; otherwise <see langword="false"/>.</returns>
        public bool TryFindShadowTrajectory(
            PlayerAvatar localPlayer,
            out Vector3 startPos,
            out Vector3 endPos,
            out float duration)
        {
            startPos = Vector3.zero;
            endPos = Vector3.zero;
            var minDur = Mathf.Max(1.5f, _config.ShadowDurationMinSeconds.Value);
            var maxDur = Mathf.Max(minDur, _config.ShadowDurationMaxSeconds.Value);
            var isStaticShadow = UnityEngine.Random.value < 0.5f;
            duration = isStaticShadow ? minDur : UnityEngine.Random.Range(minDur, maxDur);

            var camTransform = SoundLibraryService.ResolveLocalListenerTransform();
            if (camTransform == null || localPlayer == null)
            {
                return false;
            }

            var eyePos = camTransform.position;
            var forwardFlat = Vector3.ProjectOnPlane(camTransform.forward, Vector3.up).normalized;
            if (forwardFlat.sqrMagnitude < 0.01f)
            {
                forwardFlat = localPlayer.transform.forward;
            }

            var obstructMask = SemiFunc.LayerMaskGetVisionObstruct();

            // Calculate the visible distance window inside the game's active fog & camera far clip plane.
            var currentFogEnd = RenderSettings.fog ? RenderSettings.fogEndDistance : 20.0f;
            var minSpawnDist = 8.5f;
            var maxSpawnDist = Mathf.Clamp(currentFogEnd * 0.84f, 11.5f, 16.5f);

            // 1. Search distant LevelPoints (8.5m to maxSpawnDist) in front of the player with clear line of sight.
            var distantPoints = SemiFunc.LevelPointGetWithinDistance(localPlayer.transform.position, minSpawnDist, maxSpawnDist);
            if (distantPoints != null && distantPoints.Count > 0)
            {
                var startIndex = UnityEngine.Random.Range(0, distantPoints.Count);
                for (var i = 0; i < distantPoints.Count; i++)
                {
                    var lp = distantPoints[(startIndex + i) % distantPoints.Count];
                    if (lp == null)
                    {
                        continue;
                    }

                    var candidatePos = lp.transform.position;
                    var toPoint = candidatePos - eyePos;
                    var flatToPoint = Vector3.ProjectOnPlane(toPoint, Vector3.up);
                    var dist = flatToPoint.magnitude;
                    if (dist < minSpawnDist || dist > maxSpawnDist)
                    {
                        continue;
                    }

                    var angle = Vector3.Angle(forwardFlat, flatToPoint.normalized);
                    if (angle > 44.0f)
                    {
                        continue;
                    }

                    // Ensure the player has unobstructed line of sight to this distant corridor/room point.
                    var chestPos = candidatePos + Vector3.up * 1.15f;
                    if (Physics.Linecast(eyePos, chestPos, obstructMask))
                    {
                        continue;
                    }

                    var anchorFloorPos = SnapToFloor(candidatePos, obstructMask);
                    if (isStaticShadow)
                    {
                        startPos = anchorFloorPos;
                        endPos = anchorFloorPos;
                        return true;
                    }

                    BuildCorridorCrossingEndpoints(anchorFloorPos, flatToPoint.normalized, obstructMask, out startPos, out endPos);
                    return true;
                }
            }

            // 2. Fallback: Raycast down corridors ahead of the player within the visible fog window.
            for (var attempt = 0; attempt < 6; attempt++)
            {
                var sideSign = UnityEngine.Random.value < 0.5f ? -1.0f : 1.0f;
                var yawOffset = sideSign * UnityEngine.Random.Range(0.0f, 26.0f);
                var rayDir = Quaternion.Euler(0.0f, yawOffset, 0.0f) * forwardFlat;
                var targetDist = UnityEngine.Random.Range(minSpawnDist + 0.5f, maxSpawnDist);

                if (Physics.Raycast(eyePos, rayDir, out var hit, targetDist, obstructMask))
                {
                    if (hit.distance < minSpawnDist + 0.5f)
                    {
                        continue;
                    }

                    targetDist = hit.distance - 1.0f;
                }

                var rawAnchor = localPlayer.transform.position + rayDir * targetDist;
                var anchorFloorPos = SnapToFloor(rawAnchor, obstructMask);

                var chestCheck = anchorFloorPos + Vector3.up * 1.15f;
                if (Physics.Linecast(eyePos, chestCheck, obstructMask))
                {
                    continue;
                }

                if (isStaticShadow)
                {
                    startPos = anchorFloorPos;
                    endPos = anchorFloorPos;
                    return true;
                }

                BuildCorridorCrossingEndpoints(anchorFloorPos, rayDir, obstructMask, out startPos, out endPos);
                return true;
            }

            return false;
        }

        private static void BuildCorridorCrossingEndpoints(
            Vector3 centerFloorPos,
            Vector3 viewDirFlat,
            LayerMask obstructMask,
            out Vector3 startPos,
            out Vector3 endPos)
        {
            var lateralSign = UnityEngine.Random.value < 0.5f ? -1.0f : 1.0f;
            var lateralDir = Vector3.Cross(Vector3.up, viewDirFlat).normalized * lateralSign;
            var chestCenter = centerFloorPos + Vector3.up * 1.0f;

            // Find how much room there is on both sides of the corridor so it crosses cleanly from side to side.
            var backClearance = 2.4f;
            if (Physics.Raycast(chestCenter, -lateralDir, out var backHit, 2.8f, obstructMask))
            {
                backClearance = Mathf.Max(0.4f, backHit.distance - 0.35f);
            }

            var forwardClearance = 3.8f;
            if (Physics.Raycast(chestCenter, lateralDir, out var fwdHit, 4.5f, obstructMask))
            {
                forwardClearance = Mathf.Max(1.8f, fwdHit.distance - 0.25f);
            }

            startPos = SnapToFloor(centerFloorPos - lateralDir * backClearance, obstructMask);
            endPos = SnapToFloor(centerFloorPos + lateralDir * forwardClearance, obstructMask);
        }

        /// <summary>
        /// Spawns a shadow hallucination moving from <paramref name="startPos"/> to <paramref name="endPos"/> (or static when equal).
        /// </summary>
        /// <param name="modelKind">Whether to clone a player or an entity model.</param>
        /// <param name="entityIndex">Index in <see cref="EnemyDirector.enemiesSpawned"/> when <paramref name="modelKind"/> is <see cref="ShadowModelKind.Entity"/>.</param>
        /// <param name="startPos">Starting world position.</param>
        /// <param name="endPos">Ending world position.</param>
        /// <param name="duration">Total lifetime in seconds.</param>
        public void SpawnShadow(
            ShadowModelKind modelKind,
            int entityIndex,
            Vector3 startPos,
            Vector3 endPos,
            float duration)
        {
            if (!_config.Enabled.Value)
            {
                return;
            }

            var shadowRoot = new GameObject($"DYST_Shadow_{modelKind}");
            shadowRoot.transform.position = startPos;

            var moveDir = endPos - startPos;
            moveDir.y = 0.0f;
            if (moveDir.sqrMagnitude > 0.05f)
            {
                shadowRoot.transform.rotation = Quaternion.LookRotation(moveDir.normalized, Vector3.up);
            }
            else
            {
                // Static shadow: face directly toward the local player from the distance.
                var listener = SoundLibraryService.ResolveLocalListenerTransform();
                if (listener != null)
                {
                    var toPlayer = listener.position - startPos;
                    toPlayer.y = 0.0f;
                    if (toPlayer.sqrMagnitude > 0.01f)
                    {
                        shadowRoot.transform.rotation = Quaternion.LookRotation(toPlayer.normalized, Vector3.up);
                    }
                }
            }

            var visualContainer = new GameObject("Visuals");
            visualContainer.transform.SetParent(shadowRoot.transform, false);

            var shadowMaterial = CreateShadowMaterialInstance();
            var built = false;

            if (modelKind == ShadowModelKind.Entity)
            {
                built = TryBuildEntityVisuals(entityIndex, visualContainer.transform, shadowMaterial);
            }

            if (!built)
            {
                built = TryBuildPlayerVisuals(visualContainer.transform, shadowMaterial);
            }

            if (!built)
            {
                BuildFallbackHumanoidVisuals(visualContainer.transform, shadowMaterial);
            }

            var behaviour = shadowRoot.AddComponent<ShadowFigureBehaviour>();
            behaviour.Initialize(
                startPos,
                endPos,
                duration,
                _config.ShadowDirectGazeHoldSeconds.Value,
                shadowMaterial,
                visualContainer.transform);
        }

        private static Vector3 SnapToFloor(Vector3 position, LayerMask floorMask)
        {
            var origin = position + Vector3.up * 1.8f;
            if (Physics.Raycast(origin, Vector3.down, out var hit, 5.5f, floorMask))
            {
                return hit.point;
            }

            return position;
        }

        private bool TryBuildEntityVisuals(int entityIndex, Transform targetRoot, Material shadowMaterial)
        {
            try
            {
                var director = EnemyDirector.instance;
                if (director == null || director.enemiesSpawned == null || director.enemiesSpawned.Count == 0)
                {
                    return false;
                }

                var safeIndex = Mathf.Clamp(entityIndex, 0, director.enemiesSpawned.Count - 1);
                var enemyParent = director.enemiesSpawned[safeIndex];
                if (enemyParent == null)
                {
                    return false;
                }

                var searchRoot = enemyParent.EnableObject != null
                    ? enemyParent.EnableObject.transform
                    : enemyParent.transform;

                return CopyRenderersAsShadowSnapshot(searchRoot, targetRoot, shadowMaterial, maxHeightMeters: 2.6f);
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Fallback a silueta de jugador al clonar entidad: {ex.Message}");
                return false;
            }
        }

        private bool TryBuildPlayerVisuals(Transform targetRoot, Material shadowMaterial)
        {
            try
            {
                PlayerAvatar? sourceAvatar = null;
                var allPlayers = SemiFunc.PlayerGetList();
                if (allPlayers != null && allPlayers.Count > 0)
                {
                    // Prefer a non-local player avatar first because its third-person meshes are always in standard pose.
                    for (var i = 0; i < allPlayers.Count; i++)
                    {
                        var candidate = allPlayers[i];
                        if (candidate != null && !PlayerAvatarAccess.IsLocal(candidate) && candidate.playerAvatarVisuals != null)
                        {
                            sourceAvatar = candidate;
                            break;
                        }
                    }

                    if (sourceAvatar == null)
                    {
                        sourceAvatar = SemiFunc.PlayerAvatarLocal() ?? allPlayers[0];
                    }
                }
                else
                {
                    sourceAvatar = SemiFunc.PlayerAvatarLocal();
                }

                if (sourceAvatar == null || sourceAvatar.playerAvatarVisuals == null)
                {
                    return false;
                }

                var visualsRoot = sourceAvatar.playerAvatarVisuals.meshParent != null
                    ? sourceAvatar.playerAvatarVisuals.meshParent.transform
                    : sourceAvatar.playerAvatarVisuals.transform;

                return CopyRenderersAsShadowSnapshot(visualsRoot, targetRoot, shadowMaterial, maxHeightMeters: 2.1f);
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Fallback a silueta procedural al clonar jugador: {ex.Message}");
                return false;
            }
        }

        private static bool CopyRenderersAsShadowSnapshot(
            Transform sourceRoot,
            Transform targetRoot,
            Material shadowMaterial,
            float maxHeightMeters)
        {
            if (sourceRoot == null)
            {
                return false;
            }

            var renderers = sourceRoot.GetComponentsInChildren<Renderer>(includeInactive: true);
            if (renderers == null || renderers.Length == 0)
            {
                return false;
            }

            var rootWorldToLocal = sourceRoot.worldToLocalMatrix;
            var createdAny = false;
            var combinedBounds = default(Bounds);
            var hasBounds = false;
            var createdFilters = new List<MeshFilter>();

            for (var i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || (r is not SkinnedMeshRenderer && r is not MeshRenderer))
                {
                    continue;
                }

                var objName = r.gameObject.name;
                if (objName.IndexOf("Debug", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    objName.IndexOf("Canvas", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    objName.IndexOf("UI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    objName.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                Mesh? meshToUse = null;
                var isBakedMesh = false;

                if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                {
                    var baked = new Mesh
                    {
                        name = $"DYST_Baked_{smr.sharedMesh.name}"
                    };
                    smr.BakeMesh(baked);
                    meshToUse = baked;
                    isBakedMesh = true;
                }
                else if (r is MeshRenderer)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                    {
                        meshToUse = mf.sharedMesh;
                    }
                }

                if (meshToUse == null || meshToUse.vertexCount == 0)
                {
                    if (isBakedMesh && meshToUse != null)
                    {
                        UnityEngine.Object.Destroy(meshToUse);
                    }

                    continue;
                }

                var childGo = new GameObject($"Part_{i}");
                childGo.layer = 0;
                childGo.transform.SetParent(targetRoot, false);

                var relativeMatrix = rootWorldToLocal * r.transform.localToWorldMatrix;
                childGo.transform.localPosition = relativeMatrix.GetColumn(3);
                childGo.transform.localRotation = relativeMatrix.rotation;
                childGo.transform.localScale = isBakedMesh ? Vector3.one : relativeMatrix.lossyScale;

                var filter = childGo.AddComponent<MeshFilter>();
                filter.sharedMesh = meshToUse;

                var meshRenderer = childGo.AddComponent<MeshRenderer>();
                var subMeshCount = Mathf.Max(1, meshToUse.subMeshCount);
                var mats = new Material[subMeshCount];
                for (var m = 0; m < subMeshCount; m++)
                {
                    mats[m] = shadowMaterial;
                }

                meshRenderer.sharedMaterials = mats;
                meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
                meshRenderer.receiveShadows = false;
                meshRenderer.lightProbeUsage = LightProbeUsage.Off;
                meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

                if (isBakedMesh)
                {
                    childGo.AddComponent<BakedMeshCleaner>().BakedMesh = meshToUse;
                }

                var worldBounds = meshRenderer.bounds;
                if (worldBounds.size.sqrMagnitude > 0.0001f && worldBounds.size.magnitude < 12.0f)
                {
                    if (!hasBounds)
                    {
                        combinedBounds = worldBounds;
                        hasBounds = true;
                    }
                    else
                    {
                        combinedBounds.Encapsulate(worldBounds);
                    }
                }

                createdFilters.Add(filter);
                createdAny = true;
            }

            if (!createdAny)
            {
                return false;
            }

            if (hasBounds)
            {
                // Recenter the visual container so the shadow's feet rest on targetRoot's origin (0,0,0).
                var rootPos = targetRoot.position;
                var offset = new Vector3(
                    rootPos.x - combinedBounds.center.x,
                    rootPos.y - combinedBounds.min.y,
                    rootPos.z - combinedBounds.center.z);

                for (var i = 0; i < createdFilters.Count; i++)
                {
                    var t = createdFilters[i].transform;
                    t.position += offset;
                }

                if (combinedBounds.size.y > maxHeightMeters && combinedBounds.size.y > 0.1f)
                {
                    var scaleFactor = maxHeightMeters / combinedBounds.size.y;
                    targetRoot.localScale = Vector3.one * scaleFactor;
                }
            }

            return true;
        }

        private static void BuildFallbackHumanoidVisuals(Transform targetRoot, Material shadowMaterial)
        {
            CreatePrimitivePart(PrimitiveType.Capsule, targetRoot, new Vector3(0.0f, 0.9f, 0.0f), new Vector3(0.48f, 0.75f, 0.32f), shadowMaterial);
            CreatePrimitivePart(PrimitiveType.Sphere, targetRoot, new Vector3(0.0f, 1.78f, 0.0f), new Vector3(0.36f, 0.40f, 0.36f), shadowMaterial);
        }

        private static void CreatePrimitivePart(
            PrimitiveType type,
            Transform parent,
            Vector3 localPos,
            Vector3 localScale,
            Material shadowMaterial)
        {
            var go = GameObject.CreatePrimitive(type);
            go.layer = 0;
            var collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.Destroy(collider);
            }

            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;

            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = shadowMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        private Material CreateShadowMaterialInstance()
        {
            if (_shadowMaterialTemplate == null)
            {
                var shader = Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    shader = Shader.Find("Unlit/Color");
                }

                if (shader == null)
                {
                    shader = Shader.Find("Standard");
                }

                _shadowMaterialTemplate = new Material(shader)
                {
                    name = "DYST_ShadowMaterial",
                    color = new Color(0.02f, 0.02f, 0.03f, 0.88f)
                };
            }

            return new Material(_shadowMaterialTemplate);
        }

        /// <summary>
        /// Destroys a dynamically baked mesh when its GameObject is destroyed to prevent memory leaks.
        /// </summary>
        private sealed class BakedMeshCleaner : MonoBehaviour
        {
            public Mesh? BakedMesh;

            private void OnDestroy()
            {
                if (BakedMesh != null)
                {
                    Destroy(BakedMesh);
                    BakedMesh = null;
                }
            }
        }

        /// <summary>
        /// Controls the movement, static gaze, and alpha fade of a spawned shadow figure over its ~2s lifetime.
        /// </summary>
        private sealed class ShadowFigureBehaviour : MonoBehaviour
        {
            private const float BaseShadowAlpha = 0.88f;
            private Vector3 _startPos;
            private Vector3 _endPos;
            private bool _isStatic;
            private float _duration;
            private float _elapsed;
            private bool _fadingOutQuickly;
            private float _quickFadeProgress;
            private Material? _material;
            private Transform? _visualContainer;

            public void Initialize(
                Vector3 startPos,
                Vector3 endPos,
                float duration,
                float directGazeHoldSeconds,
                Material material,
                Transform visualContainer)
            {
                _startPos = startPos;
                _endPos = endPos;
                _isStatic = (endPos - startPos).sqrMagnitude < 0.05f;
                _duration = Mathf.Max(1.2f, duration);
                _material = material;
                _visualContainer = visualContainer;
                SetAlpha(0.0f);
            }

            private void Update()
            {
                var dt = Time.deltaTime;
                _elapsed += dt;

                if (_elapsed >= _duration)
                {
                    Destroy(gameObject);
                    return;
                }

                var t = Mathf.Clamp01(_elapsed / _duration);
                var currentPos = _isStatic ? _startPos : Vector3.Lerp(_startPos, _endPos, t);

                // Subtle stepping motion only when crossing a corridor; static shadows stand completely still.
                if (!_isStatic && _visualContainer != null)
                {
                    var bobOffset = Mathf.Sin(_elapsed * 9.0f) * 0.04f;
                    _visualContainer.localPosition = new Vector3(0.0f, Mathf.Abs(bobOffset), 0.0f);
                }

                transform.position = currentPos;

                var camTransform = SoundLibraryService.ResolveLocalListenerTransform();
                if (camTransform != null)
                {
                    if (_isStatic)
                    {
                        var lookDir = camTransform.position - currentPos;
                        lookDir.y = 0.0f;
                        if (lookDir.sqrMagnitude > 0.01f)
                        {
                            transform.rotation = Quaternion.LookRotation(lookDir.normalized, Vector3.up);
                        }
                    }

                    if (!_fadingOutQuickly)
                    {
                        var dist = Vector3.Distance(camTransform.position, currentPos + Vector3.up * 1.0f);
                        if (dist < 3.8f)
                        {
                            _fadingOutQuickly = true;
                        }
                    }
                }

                float alpha;
                if (_fadingOutQuickly)
                {
                    _quickFadeProgress += dt / 0.25f;
                    alpha = Mathf.Lerp(BaseShadowAlpha, 0.0f, Mathf.Clamp01(_quickFadeProgress));
                    if (_quickFadeProgress >= 1.0f)
                    {
                        Destroy(gameObject);
                        return;
                    }
                }
                else
                {
                    // Quick fade in (0.18s) and clean fade out during the last 0.30s of the 2s duration.
                    var fadeIn = Mathf.Clamp01(_elapsed / 0.18f);
                    var fadeOut = Mathf.Clamp01((_duration - _elapsed) / 0.30f);
                    alpha = BaseShadowAlpha * Mathf.Min(fadeIn, fadeOut);
                }

                SetAlpha(alpha);
            }

            private void OnDestroy()
            {
                if (_material != null)
                {
                    Destroy(_material);
                    _material = null;
                }
            }

            private void SetAlpha(float alpha)
            {
                if (_material == null)
                {
                    return;
                }

                var c = _material.color;
                c.a = alpha;
                _material.color = c;
            }
        }
    }
}

