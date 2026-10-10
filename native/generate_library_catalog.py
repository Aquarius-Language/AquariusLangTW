"""Generate the portable bilingual API catalog and reference; no packages/network needed.

Keep translations here and regenerate after adding host functions. Runtime and LSP
consume the same checked-in catalog. Unknown OpenGL words fail generation.
"""
from pathlib import Path
import re
import json

ROOT = Path(__file__).resolve().parent.parent
TRANSLATIONS = """
AddAngularImpulse 加入角衝量
AddForce 加入力
AddImpulse 加入衝量
Allocate 配置
Backspace 刪除末字
ByteData 位元組資料
ByteLength 位元組長度
Clear 清除
Cos 餘弦
CreateBox 建立方塊
CreateBuffer 建立緩衝區
CreateDevice 建立裝置
CreateProgram 建立程式
CreateRenderTarget 建立繪圖目標
CreateShader 建立著色器
CreateSphere 建立球體
CreateWindow 建立視窗
CreateWorld 建立世界
Cross 外積
DestroyWindow 銷毀視窗
Dispatch 派送
Dispose 釋放
Dot 內積
Draw 繪製
FloatData 浮點資料
FreeData 釋放資料
GetAngularVelocity 取得角速度
GetBodyCount 取得物體數量
GetCursorPos 取得游標位置
GetEnvironment 取得環境變數
GetFramebufferSize 取得影格緩衝區尺寸
GetGravity 取得重力
GetKey 取得按鍵
GetLinearVelocity 取得線速度
GetMouseButton 取得滑鼠按鈕
GetPosition 取得位置
GetRotation 取得旋轉
GetTime 取得時間
GetWindowSize 取得視窗尺寸
Identity 單位矩陣
Init 初始化
Inverse 反矩陣
IsActive 是否作用中
Load 載入
LookAt 看向
MakeContextCurrent 設為目前繪圖環境
Multiply 相乘
Normalize 正規化
OptimizeBroadPhase 最佳化廣域碰撞
PVector 向量
ParseInteger 解析整數
Perspective 透視
Poll 輪詢
PollEvents 輪詢事件
ProgramLog 程式紀錄
Radians 弧度
Read 讀取
ReadBytes 讀取位元組
ReadPixels 讀取像素
ReadText 讀取文字
RemoveBody 移除物體
Rotate 旋轉
Save 儲存
SavePPM 儲存PPM
Scale 縮放
Set 設定
SetAngularVelocity 設定角速度
SetCaretRect 設定插入點矩形
SetFriction 設定摩擦力
SetGravity 設定重力
SetInputMode 設定輸入模式
SetInt 設定整數
SetLinearVelocity 設定線速度
SetPosition 設定位置
SetRestitution 設定恢復係數
SetRotation 設定旋轉
SetWindowShouldClose 設定視窗關閉狀態
SetWindowSize 設定視窗尺寸
ShaderLog 著色器紀錄
Sin 正弦
Sqrt 平方根
Start 開始
Step 步進
Stop 停止
SupportsComposition 支援組字
SwapBuffers 交換緩衝區
SwapInterval 交換間隔
Terminate 終止
TransformPoint 轉換點
Translate 平移
Transpose 轉置
UIntData 無號整數資料
WindowHint 視窗提示
WindowShouldClose 視窗是否關閉
Write 寫入
abs 絕對值
acos 反餘弦
add 相加
addChild 加入子圖形
alpha 透明度
ambient 環境材質
ambientLight 環境光
angleBetween 夾角
applyMatrix 套用矩陣
arc 圓弧
array 轉陣列
asin 反正弦
atan 反正切
atan2 雙參數反正切
background 背景
beginContour 開始輪廓
beginDraw 開始繪圖
beginFrame 開始影格
beginShape 開始圖形
bezier 貝茲曲線
bezierDetail 貝茲細節
bezierPoint 貝茲點
bezierTangent 貝茲切線
bezierVertex 貝茲頂點
blendMode 混色模式
blue 藍色分量
box 方塊
brightness 亮度
camera 攝影機
ceil 向上取整
circle 圓形
clear 清除
clip 裁切
close 關閉
color 顏色
colorMode 色彩模式
cos 餘弦
constrain 限制範圍
copy 複製
createFont 建立字型
createGraphics 建立畫布
createImage 建立圖片
createShader 建立著色器
createShape 建立圖形
createVector 建立向量
cross 外積
cursor 游標
curve 曲線
curveDetail 曲線細節
curvePoint 曲線點
curveTangent 曲線切線
curveTightness 曲線張力
curveVertex 曲線頂點
degrees 角度
directionalLight 方向光
dist 距離
div 相除
dot 內積
ellipse 橢圓
ellipseMode 橢圓模式
emissive 自發光
endContour 結束輪廓
endDraw 結束繪圖
endFrame 結束影格
endShape 結束圖形
exit 結束
exp 指數
fill 填色
filter 濾鏡
floor 向下取整
frameRate 影格率
fullScreen 全螢幕
get 取得
getMatrix 取得矩陣
getVertex 取得頂點
getVertexCount 取得頂點數量
green 綠色分量
heading 方向角
hue 色相
image 圖片
imageMode 圖片模式
keyDown 按鍵是否按下
lerp 線性插值
lerpColor 色彩插值
lightFalloff 光衰減
lightSpecular 鏡面光
lights 燈光
limit 限制長度
line 線段
loadImage 載入圖片
loadPixels 載入像素
loadShader 載入著色器
log 對數
loop 持續繪圖
mag 長度
magSq 長度平方
map 映射
mask 遮罩
max 最大值
millis 毫秒
min 最小值
mult 相乘
noClip 停用裁切
noCursor 隱藏游標
noFill 停用填色
noLights 停用燈光
noLoop 停止持續繪圖
noSmooth 停用平滑
noStroke 停用描邊
noTint 停用染色
noise 雜訊
noiseDetail 雜訊細節
noiseSeed 雜訊種子
norm 範圍正規化
normal 法向量
normalize 正規化
on 註冊事件
ortho 正交投影
perspective 透視
point 點
pointLight 點光源
pop 彈出狀態
popMatrix 彈出矩陣
popStyle 彈出樣式
pow 次方
push 推入狀態
pushMatrix 推入矩陣
pushStyle 推入樣式
quad 四邊形
quadraticVertex 二次曲線頂點
random 亂數
randomGaussian 高斯亂數
randomSeed 亂數種子
radians 弧度
rect 矩形
rectMode 矩形模式
red 紅色分量
redraw 重繪
resetMatrix 重設矩陣
resetShader 重設著色器
resize 調整尺寸
rotate 旋轉
rotateX 旋轉X
rotateY 旋轉Y
rotateZ 旋轉Z
round 四捨五入
run 執行
saturation 飽和度
save 儲存
saveFrame 儲存影格
scale 縮放
set 設定
setFill 設定填色
setInt 設定整數
setMag 設定長度
setVertex 設定頂點
shader 著色器
shape 圖形
shearX 剪切X
shearY 剪切Y
shininess 光澤度
sin 正弦
size 尺寸
smooth 平滑
specular 鏡面材質
sphere 球體
sphereDetail 球體細節
spotLight 聚光燈
sq 平方
sqrt 平方根
square 正方形
startTextInput 開始文字輸入
stopTextInput 停止文字輸入
stroke 描邊
strokeCap 描邊端點
strokeJoin 描邊接角
strokeWeight 描邊粗細
sub 相減
tan 正切
text 文字
textAlign 文字對齊
textAscent 文字上緣高度
textDescent 文字下緣高度
textFont 文字字型
textInputRect 文字輸入矩形
textLeading 文字行距
textSize 文字大小
textWidth 文字寬度
texture 紋理
textureMode 紋理模式
tint 染色
translate 平移
triangle 三角形
updatePixels 更新像素
vertex 頂點
modelX 模型X
modelY 模型Y
modelZ 模型Z
screenX 螢幕X
screenY 螢幕Y
screenZ 螢幕Z
"""

# Keep GL's gl prefix and overload/type suffixes so every entry remains distinct.
GL_WORDS = """
Active 啟用 Attach 附加 Attachment 附件 Begin 開始 End 結束 Bind 綁定 Binding 綁定 Blend 混合 Blit 複製
Buffer 緩衝區 Buffers 緩衝區 Base 基底 Range 範圍 Data 資料 Sub 子
Attrib 屬性 Attribs 屬性 Vertex 頂點 Array 陣列 Arrays 陣列 Location 位置
Frag 片段 Framebuffer 影格緩衝區 Framebuffers 影格緩衝區 Renderbuffer 繪圖緩衝區 Renderbuffers 繪圖緩衝區
Sampler 取樣器 Samplers 取樣器 Texture 紋理 Textures 紋理 Tex 紋理
Color 顏色 Equation 方程式 Separate 分離 Func 函式 Check 檢查 Status 狀態
Clamp 限制 Clear 清除 Depth 深度 Stencil 模板 Client 用戶端 Wait 等待 Sync 同步
Mask 遮罩 Compile 編譯 Shader 著色器 Shaders 著色器 Compressed 壓縮 Image 影像
Copy 複製 Create 建立 Program 程式 Cull 剔除 Face 面 Delete 刪除 Queries 查詢
Query 查詢 Detach 卸除 Disable 停用 Draw 繪製 Elements 元素 Element 元素 Instanced 實例化
Enable 啟用 Enabled 已啟用 Conditional 條件 Render 繪圖 Transform 轉換 Feedback 回饋
Fence 圍欄 Finish 完成 Flush 刷新 Mapped 已映射 Layer 層 Front 正面 Gen 產生
Generate 產生 Mipmap 多級紋理 Get 取得 Uniform 全域參數 Uniforms 全域參數 Block 區塊
Name 名稱 Names 名稱 Attached 已附加 Boolean 布林 Double 倍精度 Float 浮點 Integer 整數
Pointer 指標 Parameter 參數 Parameters 參數 Error 錯誤 Index 索引 Indexed 索引化
Multisample 多重取樣 Object 物件 Info 資訊 Log 紀錄 Source 原始碼 String 字串
Level 層級 Varying 可變參數 Varyings 可變參數 Indices 索引 Hint 提示 Is 是否
Line 線 Width 寬度 Link 連結 Logic 邏輯 Op 操作 Map 映射 Multi 多重 Pixel 像素
Pixels 像素 Store 儲存 Point 點 Size 大小 Polygon 多邊形 Mode 模式 Offset 偏移
Primitive 圖元 Restart 重啟 Provoking 主導 Counter 計數器 Read 讀取 Storage 儲存空間
Sample 取樣 Coverage 覆蓋率 Scissor 裁切 Matrix 矩陣 Unmap 解除映射 Use 使用
Validate 驗證 Divisor 除數 Viewport 視埠
"""
translations = dict(line.split() for line in TRANSLATIONS.strip().splitlines())
parts = GL_WORDS.split()
gl_words = dict(zip(parts[::2], parts[1::2]))
for plural in ('Buffers','Attribs','Arrays','Framebuffers','Renderbuffers','Samplers','Textures',
               'Shaders','Queries','Elements','Uniforms','Names','Parameters','Varyings','Pixels'):
    gl_words[plural] += '集'


def chinese(name):
    if not name.startswith('gl'):
        return translations[name]
    remaining, output = name[2:], 'gl'
    words = sorted(gl_words, key=len, reverse=True)
    while remaining:
        word = next((word for word in words if remaining.startswith(word)), None)
        if word:
            output += gl_words[word]
            remaining = remaining[len(word):]
        else:
            suffix = re.match(r'(?:[0-9]+|[bdfisuvx_]+|[DINP])', remaining)
            if not suffix:
                raise ValueError(f'Untranslated OpenGL word: {name}: {remaining}')
            output += suffix[0]
            remaining = remaining[len(suffix[0]):]
    return output


entries = {}
def add(module, name, minimum, maximum=None, returns=None):
    entry = (name, chinese(name), minimum, minimum if maximum is None else maximum, returns)
    entries.setdefault(module, {})[name] = entry


pattern = re.compile(r'(Bind|PBind|PAction|Action|Mutate|Draw|Transform|Result)\(\s*(?:([\w.]+)\s*,\s*)?"(\w+)"\s*,\s*(\d+)\s*,\s*(?:(\d+)\s*,)?')
for file in sorted((ROOT / 'AquariusDesktop/graphics').glob('*.cs')):
    text = file.read_text(encoding='utf-8-sig')
    for match in pattern.finditer(text):
        kind, env, name, minimum, maximum = match.groups()
        pos = match.start()
        stem = file.stem
        if stem == 'GlfwModule': module = 'GLAD' if env == 'glad' else 'GLFW'
        elif stem == 'GlHelpers': module = 'GL'
        elif stem == 'MathModule': module = 'GLM'
        elif stem == 'ImageModule': module = 'STBImage'
        elif stem == 'TextInput': module = 'TextInput'
        elif stem == 'WgpuModule':
            module = {'bufferEnv': 'WGPU.Buffer', 'shaderEnv': 'WGPU.Shader', 'targetEnv': 'WGPU.Target'}.get(env,
                'WGPU' if pos < text.index('private ModuleObj CreateWgpuDevice') else 'WGPU.Device')
        elif stem.startswith('Processing'):
            module = 'Processing'
            if stem == 'ProcessingMath' and pos > text.index('private ModuleObj CreateVector'): module = 'Processing.Vector'
            if stem == 'ProcessingShapes' and env == 'e': module = 'Processing.Shape'
            if stem == 'ProcessingShaders' and pos > text.index('private ModuleObj CreateProcessingShader'): module = 'Processing.Shader'
            if stem == 'ProcessingPixels' and text.index('private ProcessingImage RegisterImage') < pos < text.index('private ProcessingImage LoadProcessingImage'): module = 'Processing.Image'
            if stem == 'ProcessingModule' and env == 'canvas.Module._Environment': module = 'Processing.Canvas'
        else: continue
        returns = {
            ('WGPU','CreateDevice'): 'WGPU.Device', ('WGPU.Device','CreateBuffer'): 'WGPU.Buffer',
            ('WGPU.Device','CreateShader'): 'WGPU.Shader', ('WGPU.Device','CreateRenderTarget'): 'WGPU.Target',
            ('Processing','createVector'): 'Processing.Vector', ('Processing','PVector'): 'Processing.Vector',
            ('Processing','createImage'): 'Processing.Image', ('Processing','loadImage'): 'Processing.Image',
            ('Processing','createGraphics'): 'Processing.Canvas', ('Processing','createShape'): 'Processing.Shape',
            ('Processing','createShader'): 'Processing.Shader', ('Processing','loadShader'): 'Processing.Shader',
        }.get((module,name))
        if module == 'Processing.Vector' and (kind == 'Mutate' or name in ('copy','cross')): returns = module
        if module == 'Processing.Image' and name == 'get': returns = module
        add(module,name,int(minimum),int(maximum) if maximum else None,returns)
        if module == 'Processing' and stem in ('ProcessingDrawing','ProcessingText','ProcessingPixels','ProcessingShaders','ProcessingShapes'):
            add('Processing.Canvas',name,int(minimum),int(maximum) if maximum else None,returns)

physics = (ROOT / 'AquariusCore/physics/PhysicsRuntime.cs').read_text(encoding='utf-8-sig')
for match in pattern.finditer(physics):
    _, _, name, minimum, maximum = match.groups()
    module = 'Jolt' if match.start() < physics.index('private ModuleObj CreateWorld') else 'Jolt.World'
    add(module,name,int(minimum),returns='Jolt.World' if name == 'CreateWorld' else None)

gl = (ROOT / 'AquariusDesktop/graphics/GlBindings.Generated.cs').read_text()
for name, args in re.findall(r'BindGl\("(gl\w+)".*?new string\[\] \{(.*?)\}', gl):
    add('GL',name,len(re.findall(r'"[^"]+"',args)))

# Functions registered through loops rather than literal Bind calls.
for name in 'abs ceil floor round sqrt sq exp log sin cos tan asin acos atan radians degrees'.split(): add('Processing',name,1)
for name in ('min','max'): add('Processing',name,1,64)
for name in 'red green blue alpha hue saturation brightness'.split():
    for module in ('Processing','Processing.Canvas'): add(module,name,1)
for name in ('bezier','curve'):
    for module in ('Processing','Processing.Canvas'): add(module,name,8,12)
for name in 'modelX modelY modelZ screenX screenY screenZ'.split():
    for module in ('Processing','Processing.Canvas'): add(module,name,2,3)

quote = lambda s: 'null' if s is None else json.dumps(s,ensure_ascii=False)
lines = ['// Generated by native/generate_library_catalog.py; do not edit.',
         'namespace AquariusLang.runtime;', 'public static partial class LibraryCatalog {',
         '    private static readonly LibraryFunction[] definitions = {']
docs = ['# Bilingual library function reference', '',
        'Both names call the same function. Module names, constants, properties and event strings retain their existing spelling.',
        'OpenGL entry points retain the `gl` prefix and overload/type suffixes.', '']
for module, functions in sorted(entries.items()):
    seen = set()
    docs += [f'## {module}', '', '| English | 繁體中文 | Arguments |', '| --- | --- | --- |']
    for name, zh, minimum, maximum, returns in sorted(functions.values()):
        if zh in seen: raise ValueError(f'Alias collision in {module}: {zh}')
        seen.add(zh)
        lines.append(f'        new({quote(module)}, {quote(name)}, {quote(zh)}, {minimum}, {maximum}, {quote(returns)}),')
        arity = str(minimum) if minimum == maximum else f'{minimum}..{maximum}'
        docs.append(f'| `{name}` | `{zh}` | {arity} |')
    docs.append('')
lines += ['    };', '}']
(ROOT / 'AquariusCore/runtime/LibraryCatalog.Generated.cs').write_text('\n'.join(lines)+'\n',encoding='utf-8')
(ROOT / 'AquariusDesktop/LIBRARY_NAMES.md').write_text('\n'.join(docs),encoding='utf-8')
print(f'Generated {sum(len(x) for x in entries.values())} API definitions in {len(entries)} library types.')
