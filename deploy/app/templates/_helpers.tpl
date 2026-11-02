{{/* The Secret of a role. deploy/release.ps1 Get-SecretScript makes it. */}}
{{- define "bt.secret" -}}
batch-tracker-{{ . }}
{{- end -}}

{{/* Args: root, name, valuesKey, role, image, probes, preStopSeconds, secrets (true when the role has a Secret), metrics (true when the role serves /metrics), extraEnv, strategy, serviceAccount, scaled. */}}
{{- define "bt.deployment" -}}
{{- $values := required (printf "caps.json entry missing: %s" .name) (index .root.Values .valuesKey) -}}
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ .name }}
  labels:
    app.kubernetes.io/name: batch-tracker
    app.kubernetes.io/component: {{ .name }}
    batch-tracker/role: {{ .role }}
spec:
  {{- if not .scaled }}
  replicas: {{ required (printf "caps.json replicas missing: %s" .name) $values.replicas }}
  {{- end }}
  strategy:
    {{- if .strategy }}
    type: {{ .strategy }}
    {{- else }}
    # A pod goes down only after its replacement is ready.
    type: RollingUpdate
    rollingUpdate:
      maxSurge: 1
      maxUnavailable: 0
    {{- end }}
  selector:
    matchLabels:
      app.kubernetes.io/component: {{ .name }}
  template:
    metadata:
      labels:
        app.kubernetes.io/name: batch-tracker
        app.kubernetes.io/component: {{ .name }}
        batch-tracker/role: {{ .role }}
      annotations:
        checksum/config: {{ include (print .root.Template.BasePath "/config.yaml") .root | sha256sum }}
        {{- with .root.Values.rolloutMarker }}
        batch-tracker/rollout-marker: {{ . | quote }}
        {{- end }}
        {{- if .metrics }}
        batch-tracker/scrape: "true"
        {{- end }}
        {{- if .secrets }}
        {{- with .root.Values.secretRevision }}
        batch-tracker/secret-revision: {{ . | quote }}
        {{- end }}
        {{- end }}
    spec:
      # ShutdownRules: 5 s drain and 20 s to finish.
      terminationGracePeriodSeconds: 30
      serviceAccountName: {{ .serviceAccount | default "default" }}
      automountServiceAccountToken: {{ if .serviceAccount }}true{{ else }}false{{ end }}
      nodeSelector:
        {{- required (printf "caps.json node missing: %s" .name) $values.nodeSelector | toYaml | nindent 8 }}
      securityContext:
        runAsNonRoot: true
        seccompProfile:
          type: RuntimeDefault
      containers:
        - name: {{ .role }}
          image: {{ required (printf "image missing: %s" .name) .image }}
          imagePullPolicy: Never
          ports:
            - containerPort: 8080
          envFrom:
            - configMapRef:
                name: batch-tracker-config
            {{- if .secrets }}
            - secretRef:
                name: {{ include "bt.secret" .role }}
            {{- end }}
          env:
            - name: POD_NAME
              valueFrom:
                fieldRef:
                  fieldPath: metadata.name
            - name: POD_NAMESPACE
              valueFrom:
                fieldRef:
                  fieldPath: metadata.namespace
            {{- with .extraEnv }}
            {{- . | toYaml | nindent 12 }}
            {{- end }}
          resources:
            {{- required (printf "caps.json limits missing: %s" .name) $values.resources | toYaml | nindent 12 }}
          securityContext:
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            capabilities:
              drop: ["ALL"]
          # A CPU limit makes .NET start slowly, so startup gets its own long budget.
          startupProbe:
            {{- .probes.liveness | toYaml | nindent 12 }}
            periodSeconds: 5
            failureThreshold: 36
          livenessProbe:
            {{- .probes.liveness | toYaml | nindent 12 }}
            periodSeconds: 10
            timeoutSeconds: 3
            failureThreshold: 3
          readinessProbe:
            {{- .probes.readiness | toYaml | nindent 12 }}
            periodSeconds: 5
            timeoutSeconds: 3
          {{- with .preStopSeconds }}
          lifecycle:
            preStop:
              sleep:
                seconds: {{ . }}
          {{- end }}
          volumeMounts:
            - name: tmp
              mountPath: /tmp
      volumes:
        - name: tmp
          emptyDir:
            sizeLimit: 32Mi
{{- end -}}

{{/* Args: root, name, minReplicas, maxReplicas, scaleDownSeconds, threshold, and either ingress and service or batches. */}}
{{- define "bt.scaledObject" -}}
apiVersion: keda.sh/v1alpha1
kind: ScaledObject
metadata:
  name: {{ .name }}
spec:
  scaleTargetRef:
    name: {{ .name }}
  minReplicaCount: {{ required (printf "minReplicas missing: %s" .name) .minReplicas }}
  maxReplicaCount: {{ required (printf "caps.json replicas missing: %s" .name) .maxReplicas }}
  # The HPA default holds a spare pod for 300 s after the load has gone.
  advanced:
    horizontalPodAutoscalerConfig:
      behavior:
        scaleDown:
          stabilizationWindowSeconds: {{ required (printf "scaleDownSeconds missing: %s" .name) .scaleDownSeconds }}
  triggers:
    - type: prometheus
      metadata:
        serverAddress: {{ .root.Values.prometheusUrl }}
        # vector(0): an empty result before the first request would read as a failed trigger.
        # 20 s holds the two 10 s scrapes a rate needs, and no more, so a drop reads as soon as it can.
        {{- if .batches }}
        # New batches, not requests: a burst of calls with no new batch must add no Worker.
        query: sum(rate(bt_batches_created_total[20s])) or vector(0)
        {{- else }}
        query: sum(rate(nginx_ingress_controller_requests{ingress="{{ .ingress }}",service="{{ .service }}"}[20s])) or vector(0)
        {{- end }}
        threshold: {{ .threshold | quote }}
    {{- if not .batches }}
    - type: cpu
      metricType: Utilization
      metadata:
        value: {{ .root.Values.cpuPercent | quote }}
    {{- end }}
{{- end -}}
