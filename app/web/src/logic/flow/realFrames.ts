import type { PodReport } from "../../api/monitorTypes";
import type { Hop } from "../../api/types";

/** One batch step and the pods at that moment, as the running site sent them. */
export const REAL_TRACE = [
  {"traceId":"e226b1c6faaf411b91f4301d7aafff5e","batchId":"eda0f80520445966a1c10b4469353a5f","farmId":"farm-a","hop":"api","state":"Dose1","at":"2026-11-01T11:44:00.8633646+00:00","ok":true,"correlationId":"657717bf54194f8ab0b9033248045ae1"},
  {"traceId":"e226b1c6faaf411b91f4301d7aafff5e","batchId":"eda0f80520445966a1c10b4469353a5f","farmId":"farm-a","hop":"dispatcher","state":"Dose1","at":"2026-11-01T11:44:06.8950711+00:00","ok":true,"pod":"dispatcher-5569665f86-bgcm6","correlationId":"657717bf54194f8ab0b9033248045ae1"},
  {"traceId":"e226b1c6faaf411b91f4301d7aafff5e","batchId":"eda0f80520445966a1c10b4469353a5f","farmId":"farm-a","hop":"bus","state":"Dose1","at":"2026-11-01T11:44:06.9421112+00:00","ok":true,"pod":"dispatcher-5569665f86-bgcm6","correlationId":"657717bf54194f8ab0b9033248045ae1"},
  {"traceId":"e226b1c6faaf411b91f4301d7aafff5e","batchId":"eda0f80520445966a1c10b4469353a5f","farmId":"farm-a","hop":"worker","state":"Dose1","at":"2026-11-01T11:45:26.4206558+00:00","ok":true,"pod":"worker-node2-6c96dd9c-wn8lr","correlationId":"657717bf54194f8ab0b9033248045ae1"},
  {"traceId":"e226b1c6faaf411b91f4301d7aafff5e","batchId":"eda0f80520445966a1c10b4469353a5f","farmId":"farm-a","hop":"record","state":"Dose1","at":"2026-11-01T11:45:26.8342422+00:00","ok":true,"pod":"worker-node2-6c96dd9c-wn8lr","correlationId":"657717bf54194f8ab0b9033248045ae1"},
] as Hop[];

export const REAL_PODS = [
  {"namespace":"batch-tracker","name":"api-7f8c59c9ff-9gcjp","node":"k3s-node1","phase":"Running","ready":true},
  {"namespace":"batch-tracker","name":"api-7f8c59c9ff-v4xrd","node":"k3s-node1","phase":"Running","ready":true},
  {"namespace":"batch-tracker","name":"dispatcher-5569665f86-5jsqk","node":"k3s-node1","phase":"Running","ready":true},
  {"namespace":"batch-tracker","name":"dispatcher-5569665f86-bgcm6","node":"k3s-node1","phase":"Running","ready":true},
  {"namespace":"batch-tracker","name":"dispatcher-node2-6f557c5b57-fn52j","node":"k3s-node2","phase":"Running","ready":true},
  {"namespace":"batch-tracker","name":"live-6fddffb6bb-bz6l6","node":"k3s-node1","phase":"Running","ready":true},
  {"namespace":"batch-tracker","name":"netprobe-h8t7w","node":"k3s-node2","phase":"Running","ready":true},
  {"namespace":"batch-tracker","name":"netprobe-t5tpj","node":"k3s-node1","phase":"Running","ready":true},
  {"namespace":"batch-tracker","name":"web-6f4955bfb8-n6jtx","node":"k3s-node1","phase":"Running","ready":true},
  {"namespace":"batch-tracker","name":"worker-58dbbf565b-2h6ml","node":"k3s-node1","phase":"Running","ready":true},
  {"namespace":"batch-tracker","name":"worker-node2-6c96dd9c-wn8lr","node":"k3s-node2","phase":"Running","ready":true},
  {"namespace":"ingress-nginx","name":"ingress-nginx-controller-c8b6f858c-fw96v","node":"k3s-node2","phase":"Running","ready":true},
  {"namespace":"keda","name":"keda-admission-webhooks-5dd94bbbd8-wcztb","node":"k3s-node2","phase":"Running","ready":true},
  {"namespace":"keda","name":"keda-operator-bbf479cd8-9rdpw","node":"k3s-node2","phase":"Running","ready":true},
  {"namespace":"keda","name":"keda-operator-metrics-apiserver-b7b5df44-67kgv","node":"k3s-node2","phase":"Running","ready":true},
  {"namespace":"kube-system","name":"coredns-7cfb7bc9c7-jz965","node":"k3s-node1","phase":"Running","ready":true},
  {"namespace":"kube-system","name":"local-path-provisioner-77b9867795-hbnc6","node":"k3s-node2","phase":"Running","ready":true},
  {"namespace":"kube-system","name":"metrics-server-7d4589576b-pwvhg","node":"k3s-node1","phase":"Running","ready":true},
  {"namespace":"metallb-system","name":"metallb-controller-75b55d45fd-zrp5w","node":"k3s-node2","phase":"Running","ready":true},
  {"namespace":"metallb-system","name":"metallb-speaker-gj9xq","node":"k3s-node1","phase":"Running","ready":true},
  {"namespace":"metallb-system","name":"metallb-speaker-sw2k8","node":"k3s-node2","phase":"Running","ready":true},
  {"namespace":"prometheus","name":"prometheus-server-55d54c4b65-tqn67","node":"k3s-node2","phase":"Running","ready":true},
] as unknown as PodReport[];
