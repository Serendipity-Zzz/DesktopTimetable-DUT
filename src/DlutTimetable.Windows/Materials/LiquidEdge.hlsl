Texture2D source : register(t0); SamplerState linearClamp : register(s0);
        cbuffer Parameters : register(b0) {float4 geometry;float4 optics;float4 composite;};
        struct Vertex {float4 position:SV_POSITION;float2 uv:TEXCOORD0;};
        Vertex VS(uint id:SV_VertexID) {
            Vertex o;float2 uv=float2((id<<1)&2,id&2);
            o.position=float4(uv*float2(2,-2)+float2(-1,1),0,1);o.uv=uv;return o;
        }
        float4 PS(Vertex input):SV_TARGET {
            float2 pixelPosition=input.position.xy;float2 halfSize=geometry.xy*.5;
            float2 delta=pixelPosition-halfSize;float2 q=abs(delta)-(halfSize-geometry.w);
            float2 outside=max(q,0);float len=length(outside);
            float distance=-(len+min(max(q.x,q.y),0)-geometry.w);
            if(distance<=0||distance>=geometry.z||optics.x<=0)return 0;
            float2 normal=len>0?sign(delta)*outside/len:(q.x>q.y?float2(sign(delta.x),0):float2(0,sign(delta.y)));
            float2 bend=-normal*optics.x*sin(3.14159265*distance/geometry.z)/geometry.xy;
            float3 color=float3(source.Sample(linearClamp,input.uv+bend*(1+optics.y)).r,
                source.Sample(linearClamp,input.uv+bend).g,source.Sample(linearClamp,input.uv+bend*(1-optics.y)).b);
            float light=.035*dot(normal,optics.zw)*pow(1-distance/geometry.z,2);
            color=saturate(color+light);
            float alpha=composite.x*saturate(distance/composite.y)*saturate((geometry.z-distance)/composite.y);
            return float4(color*alpha,alpha);
        }
