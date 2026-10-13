/* Portable MediaPipe preprocessing, detector decoding, projection and tracking.
 * Implements the published BlazePose tensor contracts; all hosts run this Wasm.
 * Coordinates are normalized to the original, unmirrored frame. */
#include <stdint.h>
#define MAX_PIXELS (1920*1080)
static uint8_t rgba[MAX_PIXELS*4];
static float detector[224*224*3], boxes[2254*12], scores[2254];
static float input[256*256*3], landmarks[195], heatmap[64*64*39], flag[1], result[166];
static float previous[165], velocity[99];
static int width,height,tracked,filtered;
static float cx,cy,radius,ux,uy,now,last,last_detection;
static float absf(float x){return x<0?-x:x;}
static float minf(float a,float b){return a<b?a:b;}
static float maxf(float a,float b){return a>b?a:b;}
static float clamp(float x,float a,float b){return minf(b,maxf(a,x));}
static int finite(float x){return x==x&&absf(x)<1e20f;}
static float sigmoid(float x){
    if(!finite(x))return 0;
    x=clamp(x,-16,16); float z=-x*1.44269504089f; int n=(int)__builtin_floorf(z);
    float t=(z-n)*.69314718056f;
    float p=1+t*(1+t*(.5f+t*(.166666667f+t*(.041666667f+t*.008333333f))));
    union { uint32_t u; float f; } scale={.u=(uint32_t)(n+127)<<23};
    return 1/(1+scale.f*p);
}
static float pixel(int x,int y,int c,float border){
    return x<0||y<0||x>=width||y>=height?border:rgba[(y*width+x)*4+c];
}
static float sample(float x,float y,int c,float border){
    int ix=(int)__builtin_floorf(x),iy=(int)__builtin_floorf(y);float fx=x-ix,fy=y-iy;
    return (pixel(ix,iy,c,border)*(1-fx)+pixel(ix+1,iy,c,border)*fx)*(1-fy)
         +(pixel(ix,iy+1,c,border)*(1-fx)+pixel(ix+1,iy+1,c,border)*fx)*fy;
}
int pose_abi(void){return 1;}
int pose_ptr(int slot){
    switch(slot){case 0:return (int)rgba;case 1:return (int)detector;case 2:return (int)boxes;
    case 3:return (int)scores;case 4:return (int)input;case 5:return (int)landmarks;
    case 6:return (int)heatmap;case 7:return (int)flag;case 8:return (int)result;default:return 0;}
}
void pose_reset(void){tracked=filtered=0;last_detection=-1e9f;result[0]=0;}
int pose_begin(int w,int h,int time_bits){
    union {int i;float f;} time={.i=time_bits};
    if(w<1||h<1||w>1920||h>1920||w*h>MAX_PIXELS||!finite(time.f))return -2;
    if(w!=width||h!=height||time.f<now||time.f-now>1000)pose_reset();
    width=w;height=h;last=now;now=time.f;result[0]=0;
    // Reacquire after loss. Periodic detection also prevents confident ROI drift.
    if(!tracked){if(now-last_detection<150)return -1;return 1;}
    return now-last_detection>1500?1:0;
}
void pose_detector_input(void){
    float scale=maxf(width,height)/224.0f,px=(224-width/scale)/2,py=(224-height/scale)/2;
    for(int y=0;y<224;y++)for(int x=0;x<224;x++)for(int c=0;c<3;c++)
        detector[c*224*224+y*224+x]=(sample((x+.5f-px)*scale-.5f,(y+.5f-py)*scale-.5f,c,127.5f)/127.5f)-1;
}
static void anchor(int i,float *x,float *y){
    int grid,cell;if(i<1568){grid=28;cell=i/2;}else if(i<1960){grid=14;cell=(i-1568)/2;}else{grid=7;cell=(i-1960)/6;}
    *x=(cell%grid+.5f)/grid;*y=(cell/grid+.5f)/grid;
}
static int roi(float x,float y,float topx,float topy){
    float dx=topx-x,dy=topy-y,r=__builtin_sqrtf(dx*dx+dy*dy);
    if(!finite(x)||!finite(y)||!finite(r)||r<8||r>maxf(width,height)*4)return 0;
    cx=x;cy=y;radius=r*1.25f;ux=dx/r;uy=dy/r;return 1;
}
int pose_detect(void){
    last_detection=now;int best=-1;float score=.5f;
    for(int i=0;i<2254;i++)if(finite(scores[i])){float s=sigmoid(scores[i]);if(s>score){score=s;best=i;}}
    if(best<0){tracked=filtered=0;return 0;}
    // Weighted suppression around the strongest face box improves ROI stability.
    float ax,ay;anchor(best,&ax,&ay);float *b=boxes+best*12;
    float bx=ax*224+b[0],by=ay*224+b[1],bw=absf(b[2]),bh=absf(b[3]);
    float total=0,points[4]={0,0,0,0};
    for(int i=0;i<2254;i++){
        float s=sigmoid(scores[i]);if(s<.5f)continue;
        float x,y;anchor(i,&x,&y);float *v=boxes+i*12;x=x*224+v[0];y=y*224+v[1];
        float w=absf(v[2]),h=absf(v[3]);
        float iw=maxf(0,minf(bx+bw/2,x+w/2)-maxf(bx-bw/2,x-w/2));
        float ih=maxf(0,minf(by+bh/2,y+h/2)-maxf(by-bh/2,y-h/2));float inter=iw*ih;
        if(i!=best&&inter/maxf(1,bw*bh+w*h-inter)<.3f)continue;
        anchor(i,&x,&y);for(int k=0;k<4;k++)points[k]+=(v[4+k]+(k%2?y:x)*224)*s;total+=s;
    }
    float scale=maxf(width,height)/224.0f,px=(224-width/scale)/2,py=(224-height/scale)/2;
    filtered=0;
    tracked=roi((points[0]/total-px)*scale,(points[1]/total-py)*scale,
                (points[2]/total-px)*scale,(points[3]/total-py)*scale);
    return tracked;
}
void pose_landmark_input(void){
    float scale=radius/128;
    for(int y=0;y<256;y++)for(int x=0;x<256;x++){
        float rx=(x+.5f-128)*scale,ry=(y+.5f-128)*scale;
        float sx=cx-uy*rx-ux*ry-.5f,sy=cy+ux*rx-uy*ry-.5f;
        for(int c=0;c<3;c++)input[(y*256+x)*3+c]=sample(sx,sy,c,0)/255;
    }
}
static void project(float x,float y,float *ox,float *oy){
    float rx=(x-128)*radius/128,ry=(y-128)*radius/128;
    *ox=cx-uy*rx-ux*ry;*oy=cy+ux*rx-uy*ry;
}
int pose_finish(void){
    if(!finite(flag[0])||flag[0]<.5f||flag[0]>1){tracked=filtered=0;return 0;}
    float all[39*5];
    for(int i=0;i<39;i++){
        float x=landmarks[i*5],y=landmarks[i*5+1];
        if(!finite(x)||!finite(y)||!finite(landmarks[i*5+2])){tracked=filtered=0;return 0;}
        int col=(int)(x/4),row=(int)(y/4);
        if(x>=0&&y>=0&&col<64&&row<64){
            float sum=0,wx=0,wy=0,peak=0;
            for(int yy=maxf(0,row-3);yy<minf(64,row+4);yy++)for(int xx=maxf(0,col-3);xx<minf(64,col+4);xx++){
                float p=sigmoid(heatmap[(yy*64+xx)*39+i]);sum+=p;wx+=p*xx;wy+=p*yy;peak=maxf(peak,p);
            }
            if(peak>=.5f&&sum>0){x=4*wx/sum;y=4*wy/sum;}
        }
        project(x,y,all+i*5,all+i*5+1);
        all[i*5+2]=landmarks[i*5+2]*radius/128;
        all[i*5+3]=sigmoid(landmarks[i*5+3]);all[i*5+4]=sigmoid(landmarks[i*5+4]);
    }
    float dt=clamp((now-last)/1000,.001f,.2f);
    for(int i=0;i<33;i++)for(int c=0;c<5;c++){
        int k=i*5+c;float v=all[k]/(c==1?height:c<3?width:1);
        // Adaptive One Euro filtering: little lag during motion, stable at rest.
        if(filtered&&c<3){int j=i*3+c;float raw=(v-previous[k])/dt;
            float a=6.2831853f*dt/(1+6.2831853f*dt);velocity[j]+=a*(raw-velocity[j]);
            float cutoff=.05f+80*absf(velocity[j]);a=6.2831853f*cutoff*dt/(1+6.2831853f*cutoff*dt);
            v=previous[k]+a*(v-previous[k]);
        }else if(c<3)velocity[i*3+c]=0;
        previous[k]=v;result[k+1]=v;
    }
    result[0]=flag[0];filtered=1;
    tracked=roi(all[33*5],all[33*5+1],all[34*5],all[34*5+1]);
    return 1;
}
