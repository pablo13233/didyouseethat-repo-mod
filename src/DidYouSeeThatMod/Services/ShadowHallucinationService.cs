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
        /// Calculates a visible trajectory in front or in the peripheral vision of the local player
        /// so the player can clearly see the shadow pass by.
        /// </summary>
        /// <param name="localPlayer">The local player avatar.</param>
        /// <param name="startPos">Outputs the floor-snapped starting world position.</param>
        /// <param name="endPos">Outputs the floor-snapped ending world position.</param>
        /// <param name="duration">Outputs the duration in seconds for the shadow movement.</param>
        /// <returns><see langword="true"/> if a valid trajectory was found; otherwise <see langword="false"/>.</returns>
        public bool TryFindShadowTrajectory(
            PlayerAvatar localPlayer,
            out Vector3 startPos,
            out Vector3 endPos,
            out float duration)
        {
            startPos = Vector3.zero;
            endPos = Vector3.zero;
            var minDur = Mathf.Max(1.0f, _config.ShadowDurationMinSeconds.Value);
            var maxDur = Mathf.Max(minDur + 0.2f, _config.ShadowDurationMaxSeconds.Value);
            duration = UnityEngine.Random.Range(minDur, maxDur);

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

            // 1. First try using nearby LevelPoints in front of the player so shadows walk between real corridor/doorway nodes.
            var nearbyPoints = SemiFunc.LevelPointGetWithinDistance(localPlayer.transform.position, 5.5f, 16.5f);
            if (nearbyPoints != null && nearbyPoints.Count > 0)
            {
                var startIndex = UnityEngine.Random.Range(0, nearbyPoints.Count);
                for (var i = 0; i < nearbyPoints.Count; i++)
                {
                    var lp = nearbyPoints[(startIndex + i) % nearbyPoints.Count];
                    if (lp == null)
                    {
                        continue;
                    }

                    var candidatePos = lp.transform.position;
                    var toPoint = candidatePos - eyePos;
                    var flatToPoint = Vector3.ProjectOnPlane(toPoint, Vector3.up);
                    var dist = flatToPoint.magnitude;
                    if (dist < 5.0f || dist > 17.0f)
                    {
                        continue;
                    }

                    var angle = Vector3.Angle(forwardFlat, flatToPoint.normalized);
                    if (angle > 62.0f)
                    {
                        continue;
                    }

                    // Check that the player has line of sight to chest height at this LevelPoint.
                    var chestPos = candidatePos + Vector3.up * 1.1f;
                    if (Physics.Linecast(eyePos, chestPos, obstructMask))
                    {
                        continue;
                    }

                    startPos = SnapToFloor(candidatePos, obstructMask);

                    // Walk toward a connected LevelPoint or laterally across the player's view.
                    if (lp.ConnectedPoints != null && lp.ConnectedPoints.Count > 0)
                    {
                        var connected = lp.ConnectedPoints[UnityEngine.Random.Range(0, lp.ConnectedPoints.Count)];
                        if (connected != null)
                        {
                            var dir = (connected.transform.position - startPos);
                            dir.y = 0.0f;
                            if (dir.sqrMagnitude > 0.25f)
                            {
                                var travelDist = Mathf.Clamp(dir.magnitude, 3.2f, 6.2f);
                                endPos = SnapToFloor(startPos + dir.normalized * travelDist, obstructMask);
                                return true;
                            }
                        }
                    }

                    var lateralSign = UnityEngine.Random.value < 0.5f ? -1.0f : 1.0f;
                    var lateralDir = Vector3.Cross(Vector3.up, flatToPoint.normalized) * lateralSign;
                    endPos = SnapToFloor(startPos + lateralDir * UnityEngine.Random.Range(3.5f, 5.8f), obstructMask);
                    return true;
                }
            }

            // 2. Fallback: Raycast into the peripheral vision cone in front of the player.
            for (var attempt = 0; attempt < 6; attempt++)
            {
                var sideSign = UnityEngine.Random.value < 0.5f ? -1.0f : 1.0f;
                var yawOffset = sideSign * UnityEngine.Random.Range(16.0f, 46.0f);
                var rayDir = Quaternion.Euler(0.0f, yawOffset, 0.0f) * forwardFlat;
                var targetDist = UnityEngine.Random.Range(6.0f, 12.5f);

                if (Physics.Raycast(eyePos, rayDir, out var hit, targetDist, obstructMask))
                {
                    if (hit.distance < 4.8f)
                    {
                        continue;
                    }

                    targetDist = hit.distance - 0.85f;
                }

                var rawStart = localPlayer.transform.position + rayDir * targetDist;
                startPos = SnapToFloor(rawStart, obstructMask);

                // Walk across the player's view toward the opposite side or deeper into the corridor.
                var crossDir = Vector3.Cross(Vector3.up, rayDir) * (-sideSign);
                var moveVec = (crossDir * 0.8f + forwardFlat * 0.35f).normalized * UnityEngine.Random.Range(3.4f, 5.5f);
                endPos = SnapToFloor(startPos + moveVec, obstructMask);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Spawns a shadow hallucination moving from <paramref name="startPos"/> to <paramref name="endPos"/>.
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
            if (moveDir.sqrMagnitude > 0.01f)
            {
                shadowRoot.transform.rotation = Quaternion.LookRotation(moveDir.normalized, Vector3.up);
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
        /// Controls the movement, subtle floating bob, gaze detection, and alpha fade of a spawned shadow figure.
        /// </summary>
        private sealed class ShadowFigureBehaviour : MonoBehaviour
        {
            private const float BaseShadowAlpha = 0.88f;
            private Vector3 _startPos;
            private Vector3 _endPos;
            private float _duration;
            private float _directGazeHoldSeconds;
            private float _elapsed;
            private float _directGazeTimer;
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
                _duration = Mathf.Max(1.2f, duration);
                _directGazeHoldSeconds = Mathf.Max(0.35f, directGazeHoldSeconds);
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
                var currentPos = Vector3.Lerp(_startPos, _endPos, t);

                // Subtle eerie stepping/gliding vertical oscillation.
                if (_visualContainer != null)
                {
                    var bobOffset = Mathf.Sin(_elapsed * 7.5f) * 0.045f;
                    _visualContainer.localPosition = new Vector3(0.0f, Mathf.Abs(bobOffset), 0.0f);
                }

                transform.position = currentPos;

                // Check if the local player is staring directly at the shadow or gets too close.
                var camTransform = SoundLibraryService.ResolveLocalListenerTransform();
                if (camTransform != null && !_fadingOutQuickly)
                {
                    var shadowCenter = currentPos + Vector3.up * 1.0f;
                    var toShadow = shadowCenter - camTransform.position;
                    var dist = toShadow.magnitude;

                    if (dist < 2.3f)
                    {
                        _fadingOutQuickly = true;
                    }
                    else if (dist < 20.0f)
                    {
                        var dot = Vector3.Dot(camTransform.forward, toShadow.normalized);
                        if (dot > 0.88f)
                        {
                            _directGazeTimer += dt;
                            if (_directGazeTimer >= _directGazeHoldSeconds)
                            {
                                _fadingOutQuickly = true;
                            }
                        }
                    }
                }

                float alpha;
                if (_fadingOutQuickly)
                {
                    _quickFadeProgress += dt / 0.45f;
                    alpha = Mathf.Lerp(BaseShadowAlpha, 0.0f, Mathf.Clamp01(_quickFadeProgress));
                    if (_quickFadeProgress >= 1.0f)
                    {
                        Destroy(gameObject);
                        return;
                    }
                }
                else
                {
                    // Smooth fade in during first 0.35s and fade out during last 0.55s.
                    var fadeIn = Mathf.Clamp01(_elapsed / 0.35f);
                    var fadeOut = Mathf.Clamp01((_duration - _elapsed) / 0.55f);
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

