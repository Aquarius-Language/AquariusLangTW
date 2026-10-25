/* Portable MediaPipe preprocessing, detector decoding, projection and tracking.
 * Implements the published BlazePose tensor contracts; every target runs this code (formerly compiled to pose.wasm,
 * now compiled into the library with its state in a struct per tracker instead of module globals).
 * Coordinates are normalized to the original, unmirrored frame. */
#include "pose.h"
/* Plain IEEE single-precision arithmetic as in WebAssembly: no fused multiply-add on targets that have it, so every
   target produces the same landmarks. */
#pragma STDC FP_CONTRACT OFF

static float absf(float x){return x<0?-x:x;}
static float minf(float a,float b){return a<b?a:b;}
static float maxf(float a,float b){return a>b?a:b;}
static float clamp(float x,float a,float b){return minf(b,maxf(a,x));}
static int finite(float x){return x==x&&absf(x)<1e20f;}
static float sigmoid(float x){
    if(!finite(x))return 0;
    x=clamp(x,-16,16); float z=-x*1.44269504089f; int n=(int)__builtin_floorf(z);
    float t=(z-n)*.69314718056f;
    float q=1+t*(1+t*(.5f+t*(.166666667f+t*(.041666667f+t*.008333333f))));
    union { uint32_t u; float f; } scale={.u=(uint32_t)(n+127)<<23};
    return 1/(1+scale.f*q);
}
static float pixel(const pose *p,int x,int y,int c,float border){
    return x<0||y<0||x>=p->width||y>=p->height?border:p->rgba[(y*p->width+x)*4+c];
}
static float sample(const pose *p,float x,float y,int c,float border){
    int ix=(int)__builtin_floorf(x),iy=(int)__builtin_floorf(y);float fx=x-ix,fy=y-iy;
    return (pixel(p,ix,iy,c,border)*(1-fx)+pixel(p,ix+1,iy,c,border)*fx)*(1-fy)
         +(pixel(p,ix,iy+1,c,border)*(1-fx)+pixel(p,ix+1,iy+1,c,border)*fx)*fy;
}
void pose_reset(pose *p){p->tracked=p->filtered=0;p->last_detection=-1e9f;p->result[0]=0;}
int pose_begin(pose *p,int w,int h,float time){
    if(w<1||h<1||w>1920||h>1920||w*h>POSE_MAX_PIXELS||!finite(time))return -2;
    if(w!=p->width||h!=p->height||time<p->now||time-p->now>1000)pose_reset(p);
    p->width=w;p->height=h;p->last=p->now;p->now=time;p->result[0]=0;
    // Reacquire after loss. Periodic detection also prevents confident ROI drift.
    if(!p->tracked){if(p->now-p->last_detection<150)return -1;return 1;}
    return p->now-p->last_detection>1500?1:0;
}
void pose_detector_input(pose *p){
    float scale=maxf(p->width,p->height)/224.0f,px=(224-p->width/scale)/2,py=(224-p->height/scale)/2;
    for(int y=0;y<224;y++)for(int x=0;x<224;x++)for(int c=0;c<3;c++)
        p->detector[c*224*224+y*224+x]=(sample(p,(x+.5f-px)*scale-.5f,(y+.5f-py)*scale-.5f,c,127.5f)/127.5f)-1;
}
static void anchor(int i,float *x,float *y){
    int grid,cell;if(i<1568){grid=28;cell=i/2;}else if(i<1960){grid=14;cell=(i-1568)/2;}else{grid=7;cell=(i-1960)/6;}
    *x=(cell%grid+.5f)/grid;*y=(cell/grid+.5f)/grid;
}
static int roi(pose *p,float x,float y,float topx,float topy){
    float dx=topx-x,dy=topy-y,r=__builtin_sqrtf(dx*dx+dy*dy);
    if(!finite(x)||!finite(y)||!finite(r)||r<8||r>maxf(p->width,p->height)*4)return 0;
    p->cx=x;p->cy=y;p->radius=r*1.25f;p->ux=dx/r;p->uy=dy/r;return 1;
}
int pose_detect(pose *p){
    p->last_detection=p->now;int best=-1;float score=.5f;
    for(int i=0;i<POSE_ANCHORS;i++)if(finite(p->scores[i])){float s=sigmoid(p->scores[i]);if(s>score){score=s;best=i;}}
    if(best<0){p->tracked=p->filtered=0;return 0;}
    // Weighted suppression around the strongest face box improves ROI stability.
    float ax,ay;anchor(best,&ax,&ay);float *b=p->boxes+best*12;
    float bx=ax*224+b[0],by=ay*224+b[1],bw=absf(b[2]),bh=absf(b[3]);
    float total=0,points[4]={0,0,0,0};
    for(int i=0;i<POSE_ANCHORS;i++){
        float s=sigmoid(p->scores[i]);if(s<.5f)continue;
        float x,y;anchor(i,&x,&y);float *v=p->boxes+i*12;x=x*224+v[0];y=y*224+v[1];
        float w=absf(v[2]),h=absf(v[3]);
        float iw=maxf(0,minf(bx+bw/2,x+w/2)-maxf(bx-bw/2,x-w/2));
        float ih=maxf(0,minf(by+bh/2,y+h/2)-maxf(by-bh/2,y-h/2));float inter=iw*ih;
        if(i!=best&&inter/maxf(1,bw*bh+w*h-inter)<.3f)continue;
        anchor(i,&x,&y);for(int k=0;k<4;k++)points[k]+=(v[4+k]+(k%2?y:x)*224)*s;total+=s;
    }
    float scale=maxf(p->width,p->height)/224.0f,px=(224-p->width/scale)/2,py=(224-p->height/scale)/2;
    p->filtered=0;
    p->tracked=roi(p,(points[0]/total-px)*scale,(points[1]/total-py)*scale,
                   (points[2]/total-px)*scale,(points[3]/total-py)*scale);
    return p->tracked;
}
void pose_landmark_input(pose *p){
    float scale=p->radius/128;
    for(int y=0;y<256;y++)for(int x=0;x<256;x++){
        float rx=(x+.5f-128)*scale,ry=(y+.5f-128)*scale;
        float sx=p->cx-p->uy*rx-p->ux*ry-.5f,sy=p->cy+p->ux*rx-p->uy*ry-.5f;
        for(int c=0;c<3;c++)p->input[(y*256+x)*3+c]=sample(p,sx,sy,c,0)/255;
    }
}
static void project(const pose *p,float x,float y,float *ox,float *oy){
    float rx=(x-128)*p->radius/128,ry=(y-128)*p->radius/128;
    *ox=p->cx-p->uy*rx-p->ux*ry;*oy=p->cy+p->ux*rx-p->uy*ry;
}
int pose_finish(pose *p){
    if(!finite(p->flag[0])||p->flag[0]<.5f||p->flag[0]>1){p->tracked=p->filtered=0;return 0;}
    float all[39*5];
    for(int i=0;i<39;i++){
        float x=p->landmarks[i*5],y=p->landmarks[i*5+1];
        if(!finite(x)||!finite(y)||!finite(p->landmarks[i*5+2])){p->tracked=p->filtered=0;return 0;}
        int col=(int)(x/4),row=(int)(y/4);
        if(x>=0&&y>=0&&col<64&&row<64){
            float sum=0,wx=0,wy=0,peak=0;
            for(int yy=maxf(0,row-3);yy<minf(64,row+4);yy++)for(int xx=maxf(0,col-3);xx<minf(64,col+4);xx++){
                float q=sigmoid(p->heatmap[(yy*64+xx)*39+i]);sum+=q;wx+=q*xx;wy+=q*yy;peak=maxf(peak,q);
            }
            if(peak>=.5f&&sum>0){x=4*wx/sum;y=4*wy/sum;}
        }
        project(p,x,y,all+i*5,all+i*5+1);
        all[i*5+2]=p->landmarks[i*5+2]*p->radius/128;
        all[i*5+3]=sigmoid(p->landmarks[i*5+3]);all[i*5+4]=sigmoid(p->landmarks[i*5+4]);
    }
    float dt=clamp((p->now-p->last)/1000,.001f,.2f);
    for(int i=0;i<33;i++)for(int c=0;c<5;c++){
        int k=i*5+c;float v=all[k]/(c==1?p->height:c<3?p->width:1);
        // Adaptive One Euro filtering: little lag during motion, stable at rest.
        if(p->filtered&&c<3){int j=i*3+c;float raw=(v-p->previous[k])/dt;
            float a=6.2831853f*dt/(1+6.2831853f*dt);p->velocity[j]+=a*(raw-p->velocity[j]);
            float cutoff=.05f+80*absf(p->velocity[j]);a=6.2831853f*cutoff*dt/(1+6.2831853f*cutoff*dt);
            v=p->previous[k]+a*(v-p->previous[k]);
        }else if(c<3)p->velocity[i*3+c]=0;
        p->previous[k]=v;p->result[k+1]=v;
    }
    p->result[0]=p->flag[0];p->filtered=1;
    p->tracked=roi(p,all[33*5],all[33*5+1],all[34*5],all[34*5+1]);
    return 1;
}
